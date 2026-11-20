using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// <see cref="FleetDoctrineReport"/> — what the Fleet window shows.
    ///
    /// <para>The rule these tests exist to hold is "never a second computation": the report's Have,
    /// Flying and Building must be the shipyard manager's own numbers, and its pressure must be
    /// attributed by the same predicate the <c>DOCTRINE_PRESSURE</c> log line uses. A window that
    /// counted for itself could tell a player a line was satisfied while the evaluator kept
    /// building, and the failure would look like a working window.</para>
    ///
    /// <para>Seeds and clocks are explicit throughout (<see cref="TestSupport.World"/> fixes both),
    /// so a demand's TTL and a fleet's arrival are decided by the test rather than by the wall
    /// clock.</para>
    /// </summary>
    public class FleetDoctrineReportTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Which roles a hull fits is read off the hull, so the report needs a catalogue to
        /// attribute pressure at all: haulers carry cargo, the gunboat is armed, the miner has a drill
        /// and (as every SE miner does) a hold. Anything unlisted fits nothing.</summary>
        private sealed class Catalogue : IPrefabCatalogue
        {
            private static readonly HashSet<string> Cargo = new HashSet<string> { "Hauler", "BigHauler", "Ridill", "Low", "High" };
            public long CargoLiters(string prefabId) { return Cargo.Contains(prefabId) ? 5000 : 0; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId)
            {
                return prefabId == "Gunboat" ? new[] { new WeaponBand { Dps = 500, RangeM = 800 } } : new WeaponBand[0];
            }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
            {
                return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();
            }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return prefabId == "Ridill" ? 1.0 : 0; }
        }

        private static WorldModel MakeWorld()
        {
            WorldModel world = TestSupport.World(seed: 7);
            world.UtcNow = Now;
            world.PrefabCatalogue = new Catalogue();
            return world;
        }

        private static FactionState MakeFaction(string tag = "EXP")
        {
            return new FactionState { Tag = tag, IsPlayerFaction = true };
        }

        private static SpatialNode AddYard(WorldModel world, string id, string tag, double x = 0)
        {
            SpatialNode n = world.Graph.Add(new Vec3(x, 0, 0), 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength = 1.0;
            n.StableStationId = 42;
            n.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return n;
        }

        private static void PostShipDemand(WorldModel world, FactionState st, string role, string nodeId,
            string destNodeId = null, bool destinedForSale = false)
        {
            world.Demand.Post(DemandEntry.ForShip(st.Tag, role, nodeId, world.UtcNow.AddHours(6),
                destNodeId: destNodeId, minCargoLiters: 1, postedUtc: world.UtcNow,
                destinedForSale: destinedForSale));
        }

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

        private static DoctrineLineReport LineNamed(FleetDoctrineReport report, string id)
        {
            foreach (DoctrineLineReport row in report.Lines)
                if (row.Line.Id == id) return row;
            return null;
        }

        // ---- counts come from the manager's own helpers -----------------------------------------

        [Fact]
        public void LineHave_IsExactlyWhatTheManagerCounts()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            SpatialNode yard = AddYard(world, "N1", st.Tag);
            yard.ShipStock["Hauler"] = 3;
            // One of the three is on the shelf: never "ours" for doctrine purposes, so both the
            // manager and the report must exclude it.
            yard.ListedForSale["Hauler"] = 1;
            AddFlight(st, world, "Hauler", 2, "N1");

            DoctrineLine line = StandingOrderFixtures.AddDoctrineLine(st, "Hauler", targetCount: 6);
            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);
            DoctrineLineReport row = LineNamed(report, line.Id);

            // The assertion is the AGREEMENT, not the literal: if the counting rule changes, both
            // sides move together and this still passes — which is the point.
            Assert.Equal(ShipyardManager.CountDoctrineHave(world, st, line), row.Have);
            Assert.Equal(ShipyardManager.CountDoctrineHullsInFlight(st, line), row.Flying);
            // 2 parked (3 less the listed one) + 2 flying.
            Assert.Equal(4, row.Have);
            Assert.Equal(2, row.Flying);
        }

        [Fact]
        public void LineBuilding_CountsQueueEntriesInScope_WhoeverQueuedThem()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            SpatialNode here = AddYard(world, "N1", st.Tag);
            SpatialNode elsewhere = AddYard(world, "N2", st.Tag, x: 5000);
            here.BuildQueue.Add(new BuildQueueEntry { PrefabId = "Hauler", Role = "Trade" });
            elsewhere.BuildQueue.Add(new BuildQueueEntry { PrefabId = "Hauler", Role = "Trade" });

            DoctrineLine scoped = StandingOrderFixtures.AddDoctrineLine(
                st, "Hauler", targetCount: 4, scopeNodeId: "N1");
            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            // Scoped to N1, so only N1's entry counts — the same scope rule the evaluator places by.
            Assert.Equal(1, LineNamed(report, scoped.Id).Building);
            Assert.Equal(ShipyardManager.CountDoctrineBuildsInProgress(world, st.Tag, scoped),
                         LineNamed(report, scoped.Id).Building);
        }

        [Fact]
        public void RouteBlocked_MirrorsTheManagersOwnBlockedState()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);
            DoctrineLine line = StandingOrderFixtures.AddDoctrineLine(st, "Hauler", targetCount: 1);

            // Not persisted and empty until the first Shipyard pass: a fresh line reads as unblocked
            // rather than as blocked, which is what the window must show after a restart.
            Assert.False(LineNamed(FleetDoctrineReport.Build(world, st), line.Id).RouteBlocked);

            line.LastBlockedLogSignature = "line=" + line.Id + " prefab=Hauler reason=route";
            Assert.True(LineNamed(FleetDoctrineReport.Build(world, st), line.Id).RouteBlocked);
        }

        [Fact]
        public void OverCeiling_ReadsTheWorldsPerformanceCeiling()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);
            DoctrineLine line = StandingOrderFixtures.AddDoctrineLine(st, "Hauler", targetCount: 1);

            Assert.False(LineNamed(FleetDoctrineReport.Build(world, st), line.Id).OverCeiling);
            world.PrefabOverCeiling = id => id == "Hauler";
            Assert.True(LineNamed(FleetDoctrineReport.Build(world, st), line.Id).OverCeiling);
        }

        // ---- pressure attribution ---------------------------------------------------------------

        [Fact]
        public void Pressure_GoesToTheHighestRankedLineWhoseScopeCoversTheNode()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);

            DoctrineLine low = StandingOrderFixtures.AddDoctrineLine(
                st, "Hauler", targetCount: 2, priority: 0);
            DoctrineLine high = StandingOrderFixtures.AddDoctrineLine(
                st, "BigHauler", targetCount: 2, priority: 9);

            PostShipDemand(world, st, "Trade", "N1", destNodeId: "N9");

            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            // The rank decides it, not the list order: the same choice ShipyardManager's pressure
            // line makes when it names a line, so the window and the log cannot disagree.
            Assert.Equal(1, LineNamed(report, high.Id).Pressure);
            Assert.Equal(0, LineNamed(report, low.Id).Pressure);
            Assert.Empty(report.Unlined);
        }

        [Fact]
        public void Pressure_ForARoleWithNoLine_IsUnlined()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);
            // A doctrine of a miner only, which fits Mine and Trade but no combat: "a role with no line
            // is not built" is the design, and this row is what keeps the omission visible rather
            // than silent.
            StandingOrderFixtures.AddDoctrineLine(st, "Ridill", targetCount: 1);

            PostShipDemand(world, st, "Raid", "N1", destNodeId: "N9");

            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            DoctrinePressureRow row = Assert.Single(report.Unlined);
            Assert.Equal("Raid", row.Role);
            Assert.Equal("N1", row.NodeId);
            Assert.Equal(1, row.Demands);
        }

        [Fact]
        public void Pressure_ACombatLineAnswersRaidAndPatrolBoth_AndSaysWhatItFits()
        {
            // A line names a hull, and an armed hull fits both combat roles: there is no role on the
            // line that could say it answers one and not the other.
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);
            DoctrineLine line = StandingOrderFixtures.AddDoctrineLine(st, "Gunboat", targetCount: 2);

            PostShipDemand(world, st, "Raid", "N1", destNodeId: "N9");
            PostShipDemand(world, st, "Patrol", "N1", destNodeId: "N9");
            PostShipDemand(world, st, "Trade", "N1", destNodeId: "N9");

            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            DoctrineLineReport row = LineNamed(report, line.Id);
            Assert.Equal(2, row.Pressure);
            Assert.Equal(new[] { "Raid", "Patrol" }, row.Fits);
            Assert.Equal("Trade", Assert.Single(report.Unlined).Role);
        }

        [Fact]
        public void Pressure_AgainstAScopedLineAtAnotherNode_IsUnlined()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);
            AddYard(world, "N2", st.Tag, x: 5000);
            StandingOrderFixtures.AddDoctrineLine(st, "Hauler", targetCount: 2, scopeNodeId: "N1");

            PostShipDemand(world, st, "Trade", "N2", destNodeId: "N9");

            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);
            Assert.Equal("N2", Assert.Single(report.Unlined).NodeId);
            Assert.Equal(0, report.Lines[0].Pressure);
        }

        [Fact]
        public void Pressure_OfAPausedLine_ReadsAsUnlined()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);
            DoctrineLine line = StandingOrderFixtures.AddDoctrineLine(
                st, "Hauler", targetCount: 2, paused: true);

            PostShipDemand(world, st, "Trade", "N1", destNodeId: "N9");

            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            // A paused line stops answering demand as well as stopping building, so the work it is
            // not doing has to show as work nobody is doing. Crediting it to the paused line would
            // read as "this line is on it" for a line that is switched off.
            Assert.Equal(0, LineNamed(report, line.Id).Pressure);
            Assert.Equal("Trade", Assert.Single(report.Unlined).Role);
        }

        [Fact]
        public void Pressure_CountsEveryDemandInTheGroup_NotTheGroup()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);
            DoctrineLine line = StandingOrderFixtures.AddDoctrineLine(st, "Hauler", targetCount: 2);

            // Three blocked routes out of one node: the number of routes waiting IS the size of the
            // pressure, which is why the destination keys the demand.
            PostShipDemand(world, st, "Trade", "N1", destNodeId: "N7");
            PostShipDemand(world, st, "Trade", "N1", destNodeId: "N8");
            PostShipDemand(world, st, "Trade", "N1", destNodeId: "N9");

            Assert.Equal(3, LineNamed(FleetDoctrineReport.Build(world, st), line.Id).Pressure);
        }

        [Fact]
        public void Pressure_ExcludesShelfDemands()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);
            DoctrineLine line = StandingOrderFixtures.AddDoctrineLine(st, "Hauler", targetCount: 2);

            PostShipDemand(world, st, "Trade", "N1", destNodeId: "N9", destinedForSale: true);

            // The store's hulls are a different purpose with a different buyer, and the evaluator
            // does not gate them. Reporting one as pressure would ask a player to raise a target
            // that would not have answered it anyway.
            Assert.Equal(0, LineNamed(FleetDoctrineReport.Build(world, st), line.Id).Pressure);
            Assert.Empty(FleetDoctrineReport.Build(world, st).Unlined);
        }

        // ---- census -----------------------------------------------------------------------------

        [Fact]
        public void Census_CountsParkedAndFlyingPerPrefab()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            SpatialNode a = AddYard(world, "N1", st.Tag);
            SpatialNode b = AddYard(world, "N2", st.Tag, x: 5000);
            a.ShipStock["Hauler"] = 2;
            b.ShipStock["Hauler"] = 1;
            b.ShipStock["Drone"] = 4;
            AddFlight(st, world, "Hauler", 2, "N1");
            AddFlight(st, world, "Drone", 1, "N2", fleetId: "F00002");

            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            Assert.Equal(2, report.Census.Count);
            // Ordered by prefab id, so two snapshots of an unchanged world read identically.
            Assert.Equal("Drone", report.Census[0].PrefabId);
            Assert.Equal(4, report.Census[0].Parked);
            Assert.Equal(1, report.Census[0].Flying);
            Assert.Equal("Hauler", report.Census[1].PrefabId);
            Assert.Equal(3, report.Census[1].Parked);
            Assert.Equal(2, report.Census[1].Flying);
        }

        [Fact]
        public void Census_Flying_AgreesWithTheManagersPerLineCount()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);
            AddFlight(st, world, "Hauler", 3, "N1");
            // A mixed squadron is credited member by member, the second of the two shapes the
            // per-line helper credits.
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Raid,
                FleetId = "F00002",
                OriginNodeId = "N1",
                ReturnNodeId = "N1",
                TargetNodeId = "N1",
                Squadron = new List<string> { "Hauler", "Nagamaki" },
                DepartedUtc = world.UtcNow,
                EstimatedArrivalUtc = world.UtcNow.AddHours(6),
            });

            // The census aggregates every prefab in one pass while the manager counts one line at a
            // time. This is the assertion that keeps the two from drifting.
            var unscoped = new DoctrineLine { Id = "FD-999", PrefabId = "Hauler", TargetCount = 1 };
            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            long censusFlying = 0;
            foreach (FleetCensusRow row in report.Census)
                if (row.PrefabId == "Hauler") censusFlying = row.Flying;

            Assert.Equal(ShipyardManager.CountDoctrineHullsInFlight(st, unscoped), censusFlying);
            Assert.Equal(4, censusFlying);
        }

        [Fact]
        public void Census_ExcludesResolvedAndInterceptedFleets()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);
            AddFlight(st, world, "Hauler", 1, "N1", fleetId: "F00001");
            // PendingFleetAction is a struct, so the flags go on at construction rather than being
            // set through the list indexer afterwards -- which would mutate a copy.
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade,
                FleetId = "F00002",
                OriginNodeId = "N1",
                ReturnNodeId = "N1",
                TargetNodeId = "N1",
                ShipPrefabId = "Hauler",
                ConvoySize = 5,
                DepartedUtc = world.UtcNow,
                EstimatedArrivalUtc = world.UtcNow.AddHours(6),
                Resolved = true,
            });
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade,
                FleetId = "F00003",
                OriginNodeId = "N1",
                ReturnNodeId = "N1",
                TargetNodeId = "N1",
                ShipPrefabId = "Hauler",
                ConvoySize = 7,
                DepartedUtc = world.UtcNow,
                EstimatedArrivalUtc = world.UtcNow.AddHours(6),
                Intercepted = true,
            });

            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            // A resolved fleet's hulls are already back in ShipStock and an intercepted one's are
            // gone: counting either would double or invent stock.
            Assert.Equal(1, Assert.Single(report.Census).Flying);
        }

        [Fact]
        public void Census_ExcludesShelfStock_SoItAgreesWithEveryLineBesideIt()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            SpatialNode yard = AddYard(world, "N1", st.Tag);
            yard.ShipStock["Hauler"] = 3;
            yard.ListedForSale["Hauler"] = 3;

            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            // Entirely on the shelf, so entirely not ours: the row would otherwise say the faction
            // owns three of a hull no line can ever count.
            Assert.Empty(report.Census);
        }

        // ---- shape ------------------------------------------------------------------------------

        [Fact]
        public void EmptyDoctrine_StillReportsPressureAndCensus()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            SpatialNode yard = AddYard(world, "N1", st.Tag);
            yard.ShipStock["Drone"] = 6;
            PostShipDemand(world, st, "Trade", "N1", destNodeId: "N9");

            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            // The state the window is opened in: no doctrine yet, and the two things a player needs
            // to see before writing one are what is waiting and what they already have.
            Assert.Empty(report.Lines);
            Assert.Equal("Trade", Assert.Single(report.Unlined).Role);
            Assert.Equal("Drone", Assert.Single(report.Census).PrefabId);
        }

        [Fact]
        public void LinesAreInEvaluationOrder()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            AddYard(world, "N1", st.Tag);
            StandingOrderFixtures.AddDoctrineLine(st, "Low", targetCount: 1, priority: 0);
            StandingOrderFixtures.AddDoctrineLine(st, "High", targetCount: 1, priority: 9);

            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            // Highest rank first, exactly as ServiceDoctrine evaluates: the window lists them in the
            // order they win a contested shipyard.
            Assert.Equal("High", report.Lines[0].Line.PrefabId);
            Assert.Equal("Low", report.Lines[1].Line.PrefabId);
        }

        [Fact]
        public void PressureAtANodeTheFactionNoLongerOwns_IsNotReported()
        {
            WorldModel world = MakeWorld();
            FactionState st = MakeFaction();
            SpatialNode lost = AddYard(world, "N1", st.Tag);
            StandingOrderFixtures.AddDoctrineLine(st, "Hauler", targetCount: 2);
            PostShipDemand(world, st, "Trade", "N1", destNodeId: "N9");

            lost.OwnerFactionTag = "OTHER";

            FleetDoctrineReport report = FleetDoctrineReport.Build(world, st);

            // A demand outliving the loss of the node it sits at is not pressure on a doctrine that
            // could no longer build there whatever its target said.
            Assert.Empty(report.Unlined);
            Assert.Equal(0, report.Lines[0].Pressure);
        }
    }
}
