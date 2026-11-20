using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Factions.Wire;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// A node's repair queue: the order its yard serves damaged hulls in (smallest outstanding bill
    /// first, binding on parts already absorbed), and the view the Builds window, the Fleet window,
    /// the Assets tab and the dashboard read it through. See docs/fleet-events.md, "Hull integrity".
    /// </summary>
    public class RepairQueueTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 29, 20, 0, 0, DateTimeKind.Utc);

        private static WorldModel Yard(out SpatialNode yard)
        {
            WorldModel w = TestSupport.World();
            w.UtcNow = T0;
            yard = w.Graph.Add(new Vec3(0, 0, 0), 1.0, "YARD");
            yard.OwnerFactionTag = "F";
            yard.HoldStrength = 1.0;
            yard.StableStationId = 1;
            yard.Capabilities[NodeCapabilityKind.Shipyard] = 1;
            return w;
        }

        private static DamagedHull Hull(SpatialNode node, string prefab, double integrity, DateTime queued,
                                        params object[] bill)
        {
            var h = new DamagedHull { PrefabId = prefab, Integrity = integrity, QueuedUtc = queued };
            for (int i = 0; i < bill.Length; i += 2) h.Bill[(string)bill[i]] = Convert.ToInt64(bill[i + 1]);
            if (node != null) node.DamagedHulls.Add(h);
            return h;
        }

        // ---- served order -----------------------------------------------------------------------

        /// <summary>The ticket's live case: a Tanto at 0.99 with a 53-unit bill landed ten minutes
        /// after a Naginata at 0.25 with a ~160k bill, and there are parts for one of them. Served
        /// first-come, the wreck soaks up the stock and the scraped hull waits; served smallest
        /// bill first, the scraped hull is back in stock after its short repair.</summary>
        [Fact]
        public void ScrapedHull_CompletesBeforeAWreck_WhenPartsAreScarce()
        {
            SpatialNode yard;
            WorldModel w = Yard(out yard);
            DamagedHull wreck = Hull(yard, "Naginata", 0.25, T0,
                "Comp:SteelPlate", 100365, "Comp:Reactor", 450, "Comp:Construction", 59435);
            DamagedHull scraped = Hull(yard, "Tanto", 0.988, T0.AddMinutes(10),
                "Comp:SteelPlate", 38, "Comp:Reactor", 1, "Comp:Construction", 14);
            w.AddNodeInventory(yard.Id, "Comp:SteelPlate", 50);
            w.AddNodeInventory(yard.Id, "Comp:Reactor", 1);
            w.AddNodeInventory(yard.Id, "Comp:Construction", 14);
            var log = new List<string>();

            HullDamage.ServiceRepairs(w, yard, "F", log);
            Assert.True(scraped.PartsComplete);
            Assert.False(wreck.PartsComplete);
            Assert.Equal(12L, wreck.Collected["Comp:SteelPlate"]);   // what was left after the scraped hull
            Assert.False(wreck.Collected.ContainsKey("Comp:Reactor"));

            w.UtcNow = w.UtcNow.Add(HullDamage.RepairTime(0.988));
            Assert.Equal(1, HullDamage.ServiceRepairs(w, yard, "F", log));
            Assert.Equal(1, yard.ShipStock["Tanto"]);
            Assert.Same(wreck, Assert.Single(yard.DamagedHulls));
            Assert.Contains(log, l => l.StartsWith("F REPAIR_DONE prefab=Tanto node=YARD integrity=0.99",
                                                  StringComparison.Ordinal));
        }

        /// <summary>The live N483 case: a Naginata that landed first absorbed four Reactors, and
        /// nine Tantos each missing one Reactor wait behind it with nothing in stock. The order is
        /// binding: four Tantos take the Naginata's four and finish; the Naginata keeps the rest.</summary>
        [Fact]
        public void AWreckThatCollectedFirst_GivesUpWhatSmallerRepairsNeed()
        {
            SpatialNode yard;
            WorldModel w = Yard(out yard);
            DamagedHull wreck = Hull(yard, "Naginata", 0.988, T0,
                "Comp:Reactor", 8, "Comp:SteelPlate", 1578, "Comp:Superconductor", 36);
            wreck.Collected["Comp:Reactor"] = 4;
            wreck.Collected["Comp:SteelPlate"] = 1578;
            var tantos = new List<DamagedHull>();
            for (int i = 0; i < 9; i++)
            {
                DamagedHull t = Hull(yard, "Tanto", 0.988, T0.AddMinutes(10), "Comp:Reactor", 1, "Comp:SteelPlate", 14);
                t.Collected["Comp:SteelPlate"] = 14;
                tantos.Add(t);
            }
            var log = new List<string>();

            HullDamage.ServiceRepairs(w, yard, "F", log);

            Assert.Equal(4, tantos.Count(t => t.PartsComplete));
            Assert.True(tantos.Take(4).All(t => t.PartsComplete));   // equal hulls keep landing order
            Assert.False(wreck.Collected.ContainsKey("Comp:Reactor"));
            Assert.Equal(1578L, wreck.Collected["Comp:SteelPlate"]);  // only what a leader needed moved
            Assert.Contains("F REPAIR_REASSIGNED node=YARD units=4 to=4 from=1", log);
            // Conservation: the four Reactors moved, none were minted or lost.
            Assert.Equal(4L, tantos.Sum(t => { long r; t.Collected.TryGetValue("Comp:Reactor", out r); return r; }));
        }

        [Fact]
        public void PartsComplete_LogsTheServedPosition()
        {
            SpatialNode yard;
            WorldModel w = Yard(out yard);
            Hull(yard, "Naginata", 0.25, T0, "Comp:SteelPlate", 1000);
            Hull(yard, "Tanto", 0.95, T0.AddMinutes(5), "Comp:SteelPlate", 20);
            w.AddNodeInventory(yard.Id, "Comp:SteelPlate", 20);
            var log = new List<string>();

            HullDamage.ServiceRepairs(w, yard, "F", log);

            Assert.Contains("F REPAIR_PARTS_COMPLETE prefab=Tanto node=YARD integrity=0.95 pos=1/2 bill_units=20 hours=0.2", log);
            Assert.DoesNotContain(log, l => l.Contains("REPAIR_PARTS_COMPLETE prefab=Naginata"));
            Assert.DoesNotContain(log, l => l.Contains("REPAIR_REASSIGNED"));
        }

        [Fact]
        public void ServedOrder_SmallestOutstanding_ThenIntegrity_ThenOldest_ThenLandingOrder()
        {
            var big = Hull(null, "A", 0.25, T0, "k", 900);
            var smallLow = Hull(null, "B", 0.50, T0, "k", 10);
            var smallHigh = Hull(null, "C", 0.90, T0.AddHours(1), "k", 10);
            var smallHighOld = Hull(null, "D", 0.90, T0, "k", 10);
            var twinA = Hull(null, "E", 0.90, T0, "k", 10);
            var halfCollected = Hull(null, "F", 0.25, T0, "k", 900);
            halfCollected.Collected["k"] = 895;   // outstanding 5: collected parts count

            var hulls = new List<DamagedHull> { big, smallLow, smallHigh, smallHighOld, twinA, halfCollected };
            List<DamagedHull> order = HullDamage.ServedOrder(hulls);
            Assert.Equal(new[] { "F", "D", "E", "C", "B", "A" }, order.Select(h => h.PrefabId).ToArray());

            // The same hulls in another landing order: only the exact tie (D, E) follows it.
            hulls.Reverse();
            order = HullDamage.ServedOrder(hulls);
            Assert.Equal(new[] { "F", "E", "D", "C", "B", "A" }, order.Select(h => h.PrefabId).ToArray());
        }

        [Fact]
        public void AParkedHullWithItsPartsIn_IsNeverDrawnOn()
        {
            SpatialNode yard;
            WorldModel w = Yard(out yard);
            DamagedHull working = Hull(yard, "Naginata", 0.5, T0, "Comp:Reactor", 3);
            working.Collected["Comp:Reactor"] = 3;
            working.PartsCompleteUtc = T0;
            DamagedHull waiting = Hull(yard, "Tanto", 0.9, T0, "Comp:Reactor", 1);

            HullDamage.ServiceRepairs(w, yard, "F", new List<string>());

            Assert.Equal(3L, working.Collected["Comp:Reactor"]);
            Assert.False(waiting.PartsComplete);
        }

        // ---- the view ---------------------------------------------------------------------------

        [Fact]
        public void QueueView_FoldsIdenticalHulls_InServedOrder_WithTheirState()
        {
            SpatialNode yard;
            WorldModel w = Yard(out yard);
            Hull(yard, "Naginata", 0.25, T0, "Comp:SteelPlate", 1000);
            for (int i = 0; i < 3; i++) Hull(yard, "Tanto", 0.7, T0.AddMinutes(1), "Comp:SteelPlate", 30);
            DamagedHull done = Hull(yard, "Clipper", 0.6, T0, "Comp:SteelPlate", 40);
            done.Collected["Comp:SteelPlate"] = 40;
            done.PartsCompleteUtc = T0.AddMinutes(30);

            List<RepairQueueRow> rows = HullDamage.QueueView(yard);

            Assert.Equal(3, rows.Count);
            Assert.Equal("Clipper", rows[0].PrefabId);
            Assert.Equal(RepairState.Repairing, rows[0].State);
            Assert.Equal(T0.AddMinutes(30) + HullDamage.RepairTime(0.6), rows[0].DoneUtc);
            Assert.Equal(40L, rows[0].CollectedUnits);
            Assert.Equal("Tanto", rows[1].PrefabId);
            Assert.Equal(3, rows[1].Count);
            Assert.Equal(2, rows[1].Position);
            Assert.Equal(RepairState.WaitingForParts, rows[1].State);
            Assert.Equal(default(DateTime), rows[1].DoneUtc);
            Assert.Equal(5, rows[2].Position);
            Assert.Equal(1000L, rows[2].BillUnits);

            yard.ShipyardPaused = true;
            Assert.All(HullDamage.QueueView(yard), r => Assert.Equal(RepairState.YardPaused, r.State));
            yard.Capabilities.Remove(NodeCapabilityKind.Shipyard);
            Assert.All(HullDamage.QueueView(yard), r => Assert.Equal(RepairState.NoShipyard, r.State));
        }

        [Fact]
        public void BillRows_ReadTheEstateLikeABuild()
        {
            SpatialNode yard;
            WorldModel w = Yard(out yard);
            SpatialNode other = w.Graph.Add(new Vec3(1000, 0, 0), 1.0, "OTHER");
            other.OwnerFactionTag = "F";
            w.AddNodeInventory(other.Id, "Comp:Reactor", 7);
            w.AddNodeInventory(yard.Id, "Comp:SteelPlate", 3);
            DamagedHull hull = Hull(yard, "Tanto", 0.9, T0, "Comp:Reactor", 2, "Comp:SteelPlate", 10);
            hull.Collected["Comp:SteelPlate"] = 5;
            var st = new FactionState { Tag = "F" };

            BuildSupply.EstateView view = BuildSupply.EstateView.For(w, st, yard);
            List<BuildSupply.ComponentRow> rows = BuildSupply.Rows(yard, hull, view);

            Assert.Equal(2, rows.Count);
            BuildSupply.ComponentRow steel = rows.Single(r => r.Key == "Comp:SteelPlate");
            Assert.Equal(10L, steel.Need);
            Assert.Equal(5L, steel.Have);
            Assert.Equal(3L, steel.AtNode);
            Assert.Equal(7L, rows.Single(r => r.Key == "Comp:Reactor").Elsewhere);
        }

        [Fact]
        public void FleetReport_CountsHullsInRepair_ByPrefab_AndPerLine()
        {
            SpatialNode yard;
            WorldModel w = Yard(out yard);
            Hull(yard, "Tanto", 0.7, T0, "Comp:SteelPlate", 30);
            Hull(yard, "Tanto", 0.7, T0, "Comp:SteelPlate", 30);
            DamagedHull working = Hull(yard, "Tanto", 0.9, T0, "Comp:SteelPlate", 10);
            working.Collected["Comp:SteelPlate"] = 10;
            working.PartsCompleteUtc = T0;
            Hull(yard, "Clipper", 0.5, T0, "Comp:SteelPlate", 50);
            var st = new FactionState { Tag = "F" };
            st.FleetDoctrine.Add(new DoctrineLine { Id = "L1", PrefabId = "Tanto", TargetCount = 4 });

            FleetDoctrineReport report = FleetDoctrineReport.Build(w, st);

            DoctrineLineReport line = Assert.Single(report.Lines);
            Assert.Equal(2, line.RepairWaiting);
            Assert.Equal(1, line.Repairing);
            // In repair is part of Have: the three Tantos are coming back, and the report's Have is
            // the evaluator's own number (ShipyardManager.CountDoctrineHave), not a second count.
            Assert.Equal(3L, line.Have);
            Assert.Equal(line.RepairWaiting + line.Repairing, line.Have - line.Flying);
            FleetCensusRow tanto = report.Census.Single(r => r.PrefabId == "Tanto");
            Assert.Equal(2, tanto.RepairWaiting);
            Assert.Equal(1, tanto.Repairing);
            FleetCensusRow clipper = report.Census.Single(r => r.PrefabId == "Clipper");   // only in repair
            Assert.Equal(0L, clipper.Parked);
            Assert.Equal(1, clipper.RepairWaiting);
        }

        // ---- wire -------------------------------------------------------------------------------

        [Fact]
        public void NodeDetailSnapshot_RoundTripsTheRepairQueue()
        {
            var sent = new WireNodeDetail
            {
                Exists = true, Id = "N483", Owned = true, RepairHulls = 11,
                Repairs = new[]
                {
                    new WireNodeRepair
                    {
                        PrefabId = "Tanto", Count = 9, Position = 1, Integrity = 0.988,
                        State = (byte)WireRepairState.WaitingForParts, BillTotal = 53, Collected = 52,
                        QueuedUtcTicks = T0.Ticks,
                        Bill = new[]
                        {
                            new WireBuildComponent { Key = "Comp:Reactor", Need = 1, Have = 0, AtNode = 0, Elsewhere = 12, EnRoute = 3, Unserved = true },
                            new WireBuildComponent { Key = "Comp:SteelPlate", Need = 14, Have = 14 },
                        },
                    },
                    new WireNodeRepair
                    {
                        PrefabId = "Naginata", Count = 1, Position = 10, Integrity = 0.25,
                        State = (byte)WireRepairState.Repairing, BillTotal = 160000, Collected = 160000,
                        QueuedUtcTicks = T0.Ticks, PartsCompleteUtcTicks = T0.AddHours(1).Ticks,
                        DoneUtcTicks = T0.AddHours(4).Ticks,
                    },
                },
            };

            WireNodeDetail got;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(sent), out got));

            Assert.Equal(11, got.RepairHulls);
            Assert.Equal(2, got.Repairs.Length);
            WireNodeRepair a = got.Repairs[0];
            Assert.Equal("Tanto", a.PrefabId);
            Assert.Equal(9, a.Count);
            Assert.Equal(1, a.Position);
            Assert.Equal(0.988, a.Integrity);
            Assert.Equal((byte)WireRepairState.WaitingForParts, a.State);
            Assert.Equal(53L, a.BillTotal);
            Assert.Equal(52L, a.Collected);
            Assert.Equal(T0.Ticks, a.QueuedUtcTicks);
            Assert.Equal(0L, a.PartsCompleteUtcTicks);
            Assert.Equal(2, a.Bill.Length);
            Assert.Equal("Comp:Reactor", a.Bill[0].Key);
            Assert.Equal(12L, a.Bill[0].Elsewhere);
            Assert.Equal(3L, a.Bill[0].EnRoute);
            Assert.True(a.Bill[0].Unserved);
            Assert.Equal(14L, a.Bill[1].Have);
            WireNodeRepair b = got.Repairs[1];
            Assert.Equal((byte)WireRepairState.Repairing, b.State);
            Assert.Equal(T0.AddHours(1).Ticks, b.PartsCompleteUtcTicks);
            Assert.Equal(T0.AddHours(4).Ticks, b.DoneUtcTicks);
            Assert.Empty(b.Bill);
        }

        [Fact]
        public void NodeDetailSnapshot_ForAnUnownedNode_CarriesNoRepairQueue()
        {
            var sent = new WireNodeDetail
            {
                Exists = true, Id = "N1", Owned = false, RepairHulls = 3,
                Repairs = new[] { new WireNodeRepair { PrefabId = "Tanto", Count = 3 } },
            };
            WireNodeDetail got;
            Assert.True(FactionsWire.TryReadNodeDetailSnapshot(FactionsWire.WriteNodeDetailSnapshot(sent), out got));
            Assert.Empty(got.Repairs);
            Assert.Equal(0, got.RepairHulls);
        }

        [Fact]
        public void AssetsAndFleetSnapshots_RoundTripRepairCounts()
        {
            var nodes = new List<WireAssetNode>
            {
                new WireAssetNode { Id = "N1", DisplayName = "One", ShipsReady = 2, BuildsQueued = 1, ShipsInRepair = 17 },
            };
            string tag, name;
            List<WireAssetNode> gotNodes;
            List<WireAssetFleet> fleets;
            List<WireAssetGrid> grids;
            Assert.True(FactionsWire.TryReadAssetsSnapshot(
                FactionsWire.WriteAssetsSnapshot("F", "F", nodes, null, null),
                out tag, out name, out gotNodes, out fleets, out grids));
            Assert.Equal(17, gotNodes[0].ShipsInRepair);
            Assert.Equal(1, gotNodes[0].BuildsQueued);

            var lines = new List<WireDoctrineLine>
            {
                new WireDoctrineLine { Id = "L1", PrefabId = "Tanto", Have = 1, RepairWaiting = 5, Repairing = 2, OverCeiling = true },
            };
            var census = new List<WireFleetCensusRow>
            {
                new WireFleetCensusRow { PrefabId = "Tanto", Parked = 1, Flying = 4, RepairWaiting = 6, Repairing = 3 },
            };
            string ftag;
            int cap, revision;
            List<WireDoctrineLine> gotLines;
            List<WireDoctrinePressureRow> unlined;
            List<WireFleetCensusRow> gotCensus;
            Assert.True(FactionsWire.TryReadFleetDoctrineSnapshot(
                FactionsWire.WriteFleetDoctrineSnapshot("F", 32, 1, lines, null, census),
                out ftag, out cap, out revision, out gotLines, out unlined, out gotCensus));
            Assert.Equal(5, gotLines[0].RepairWaiting);
            Assert.Equal(2, gotLines[0].Repairing);
            Assert.True(gotLines[0].OverCeiling);
            Assert.Equal(4L, gotCensus[0].Flying);
            Assert.Equal(6, gotCensus[0].RepairWaiting);
            Assert.Equal(3, gotCensus[0].Repairing);
        }
    }
}
