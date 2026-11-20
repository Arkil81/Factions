using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// FAC-053 coverage: a ship-loss line must say which hull was lost, on which leg, and where its
    /// resolving ratio came from. Before this, <c>HAUL_SHIP_LOST</c> carried only an actor tag and a
    /// ratio, so 102 losses across four days could not be attributed to a prefab, a node or a fleet,
    /// and a 0.00 produced by a forced grid outcome printed identically to one produced by elapsed
    /// transit time.
    /// </summary>
    public class FleetLossAttributionTests
    {
        private static WorldModel MakeWorld()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
            return world;
        }

        private static SpatialNode AddNode(WorldModel world, string id, string tag = "EXP")
        {
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength = 1.0;
            return n;
        }

        private static List<string> Resolve(FactionState st, WorldModel world)
        {
            var log = new List<string>();
            var states = new List<FactionState> { st };
            PendingFleetResolver.ResolveArrivals(states, world, TestSupport.Tuning(),
                                                 new SeededRandom(1), log);
            return log;
        }

        private static string Line(IEnumerable<string> log, string tag)
        {
            return log.FirstOrDefault(l => l.StartsWith(tag, StringComparison.Ordinal));
        }

        [Fact]
        public void HaulShipLost_NamesFleetPrefabLegAndTheWriterThatForcedTheRatio()
        {
            var world = MakeWorld();
            AddNode(world, "N048");
            AddNode(world, "N8466");

            var st = new FactionState { Tag = "EXP" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "EXP",
                OriginNodeId = "N048", TargetNodeId = "N8466",
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "Sveinn_D4H", ConvoySize = 2,
                FleetId = "63CA52",
                ForcedRatio = 0.0, ForcedRatioCause = "grid_destroyed",
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            });

            string line = Line(Resolve(st, world), "HAUL_SHIP_LOST");

            Assert.NotNull(line);
            Assert.Contains("[63CA52]", line, StringComparison.Ordinal);
            Assert.Contains("EXP", line, StringComparison.Ordinal);
            Assert.Contains("prefab=Sveinn_D4H", line, StringComparison.Ordinal);
            Assert.Contains("count=2", line, StringComparison.Ordinal);
            Assert.Contains("from=N048", line, StringComparison.Ordinal);
            Assert.Contains("to=N8466", line, StringComparison.Ordinal);
            Assert.Contains("ratio=0.00", line, StringComparison.Ordinal);
            Assert.Contains("why=forced:grid_destroyed", line, StringComparison.Ordinal);
        }

        /// <summary>FAC-061. The ratio is formatted with an explicit invariant culture, so the log
        /// keeps a dot decimal whatever locale the process happens to resolve.
        /// <para>This asserts the condition rather than inheriting it: the failure was invisible in
        /// the container (invariant globalization forces a dot) and invisible on the Windows host's
        /// own culture readout (<c>en-US</c>/<c>en-GB</c>, both dot) — it only appeared because the
        /// <c>dotnet test</c> process itself resolved <c>en-FI</c>. Reading the ambient culture
        /// proves nothing; setting a comma-decimal one does.</para></summary>
        [Fact]
        public void ShipLostRatio_KeepsADotDecimal_UnderACommaDecimalCulture()
        {
            CultureInfo previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                // Built rather than named: the hermes container runs with
                // DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1, where every named culture collapses to
                // the invariant one -- so new CultureInfo("fi-FI") would give a dot separator there
                // and this test would pass without ever constructing the condition it claims.
                var commaDecimal = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                commaDecimal.NumberFormat.NumberDecimalSeparator = ",";
                Assert.Equal(",", commaDecimal.NumberFormat.NumberDecimalSeparator);
                Assert.Equal("0,00", 0.0.ToString("0.00", commaDecimal)); // guard: the culture really does format with a comma
                Thread.CurrentThread.CurrentCulture = commaDecimal;

                var world = MakeWorld();
                AddNode(world, "N048");
                AddNode(world, "N8466");
                var st = new FactionState { Tag = "EXP" };
                st.PendingFleets.Add(new PendingFleetAction
                {
                    Kind = ActionKind.Haul, ActorTag = "EXP",
                    OriginNodeId = "N048", TargetNodeId = "N8466",
                    DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                    ShipPrefabId = "Sveinn_D4H", ConvoySize = 2,
                    FleetId = "63CA52",
                    ForcedRatio = 0.0, ForcedRatioCause = "grid_destroyed",
                    CurrentStopKind = FleetStopKind.Service,
                    LastResolvedSegment = -1,
                });

                string line = Line(Resolve(st, world), "HAUL_SHIP_LOST");

                Assert.NotNull(line);
                Assert.Contains("ratio=0.00", line, StringComparison.Ordinal);
                Assert.DoesNotContain("ratio=0,00", line, StringComparison.Ordinal);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Fact]
        public void ShipLost_CarriesTheSameAttribution_ForANonHaulMission()
        {
            var world = MakeWorld();
            AddNode(world, "N048");
            AddNode(world, "N4715", "RIVAL");

            var st = new FactionState { Tag = "EXP" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Trade, ActorTag = "EXP", TargetTag = "RIVAL",
                OriginNodeId = "N048", TargetNodeId = "N4715",
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "C55_Derelict_CommercialHopper", ConvoySize = 1,
                FleetId = "93BDFE",
                ForcedRatio = 0.0, ForcedRatioCause = "grid_stalled",
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            });

            string line = Line(Resolve(st, world), "SHIP_LOST");

            Assert.NotNull(line);
            Assert.Contains("[93BDFE]", line, StringComparison.Ordinal);
            Assert.Contains("prefab=C55_Derelict_CommercialHopper", line, StringComparison.Ordinal);
            Assert.Contains("from=N048", line, StringComparison.Ordinal);
            Assert.Contains("to=N4715", line, StringComparison.Ordinal);
            Assert.Contains("why=forced:grid_stalled", line, StringComparison.Ordinal);
        }

        [Fact]
        public void FerryLost_KeepsItsRole_AndGainsTheAttributionSuffix()
        {
            var world = MakeWorld();
            AddNode(world, "N048");
            AddNode(world, "N8466");

            var st = new FactionState { Tag = "EXP" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Ferry, ActorTag = "EXP",
                OriginNodeId = "N048", TargetNodeId = "N8466",
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "GullinburstiAtmo", ConvoySize = 1,
                FerryRole = "Mine", FleetId = "71B0BD",
                ForcedRatio = 0.0, ForcedRatioCause = "grid_stalled",
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            });

            string line = Line(Resolve(st, world), "FERRY_LOST");

            Assert.NotNull(line);
            Assert.Contains("role=Mine", line, StringComparison.Ordinal);
            Assert.Contains("prefab=GullinburstiAtmo", line, StringComparison.Ordinal);
            Assert.Contains("why=forced:grid_stalled", line, StringComparison.Ordinal);
        }

        [Fact]
        public void ForcedRatioWithNoCause_ReadsAsUnknown_NotAsAnElapsedRatio()
        {
            // A fleet persisted before ForcedRatioCause existed still carries ForcedRatio. The line
            // must not claim the ratio came from elapsed transit time just because the cause is
            // absent -- that would attribute a forced loss to the wrong mechanism entirely.
            var world = MakeWorld();
            AddNode(world, "N048");
            AddNode(world, "N8466");

            var st = new FactionState { Tag = "EXP" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "EXP",
                OriginNodeId = "N048", TargetNodeId = "N8466",
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "Sveinn_D4H", ConvoySize = 1,
                FleetId = "AAAAAA",
                ForcedRatio = 0.0,   // no cause: a pre-FAC-053 brain doc
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            });

            string line = Line(Resolve(st, world), "HAUL_SHIP_LOST");

            Assert.NotNull(line);
            Assert.Contains("why=forced:unknown", line, StringComparison.Ordinal);
            Assert.DoesNotContain("why=elapsed", line, StringComparison.Ordinal);
        }

        [Fact]
        public void ASuccessfulHaul_StillReturnsItsShip_AndLogsNoLoss()
        {
            // Regression guard for the attribution change: the success path is untouched.
            var world = MakeWorld();
            AddNode(world, "N048");
            AddNode(world, "N8466");

            var st = new FactionState { Tag = "EXP" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "EXP",
                OriginNodeId = "N048", TargetNodeId = "N8466",
                DepartedUtc = world.UtcNow.AddMinutes(-10), EstimatedArrivalUtc = world.UtcNow,
                ShipPrefabId = "Sveinn_D4H", ConvoySize = 1,
                FleetId = "BBBBBB",
                CurrentStopKind = FleetStopKind.Service,
                LastResolvedSegment = -1,
            });

            List<string> log = Resolve(st, world);

            Assert.Null(Line(log, "HAUL_SHIP_LOST"));
            Assert.NotNull(Line(log, "HAUL_SHIP_RETURNED"));
            int stock;
            world.Graph.Nodes["N8466"].ShipStock.TryGetValue("Sveinn_D4H", out stock);
            Assert.Equal(1, stock);
        }

        [Fact]
        public void ForcedRatioCause_SurvivesABrainRoundTrip_AndIsOmittedWhenNull()
        {
            var graph = new SpatialGraph();
            SpatialNode home = graph.Add(new Vec3(0, 0, 0), 0.6, "N048");
            home.OwnerFactionTag = "EXP"; home.HoldStrength = 0.8; home.Radius = 30;

            var st = new FactionState { Tag = "EXP" };
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "EXP",
                OriginNodeId = "N048", TargetNodeId = "N8466",
                ForcedRatio = 0.0, ForcedRatioCause = "grid_stalled",
                CurrentStopKind = FleetStopKind.Service,
            });
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Haul, ActorTag = "EXP",
                OriginNodeId = "N048", TargetNodeId = "N4715",
                CurrentStopKind = FleetStopKind.Service,
            });

            BrainDoc doc = FactionStore.ToBrainDoc(st, graph.OwnedBy("EXP"), FactionStore.DefaultLogCap);
            Assert.Equal("grid_stalled", doc.PendingFleets[0].ForcedRatioCause);
            Assert.Null(doc.PendingFleets[1].ForcedRatioCause);

            FactionState roundTripped = FactionStore.ApplyBrainDoc(doc, new SpatialGraph());
            Assert.Equal("grid_stalled", roundTripped.PendingFleets[0].ForcedRatioCause);
            Assert.Null(roundTripped.PendingFleets[1].ForcedRatioCause);
        }
    }
}
