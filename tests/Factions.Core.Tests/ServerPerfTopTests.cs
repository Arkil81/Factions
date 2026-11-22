using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class ServerPerfTopTests
    {
        private static readonly DateTime At = new DateTime(2026, 11, 21, 20, 21, 27, DateTimeKind.Utc);

        private static ServerPerfTop Window()
        {
            var t = new ServerPerfTop
            {
                AtUtc = At, WindowSeconds = 30, Frames = 1800,
                BusyMs = 18.5, PhysicsMs = 6.25, OursMs = 1.5,
                SerialMs = 4.0, ParallelCpuMs = 3.0, ParallelWallMs = 1.25,
                HasLedger = true, UnattributedPhysicsMs = 0.75,
            };
            t.Parties.Add(new ServerPerfTop.Row { Name = "Arkil", LogicMs = 0.25, PhysicsMs = 0.05, Count = 3 });
            t.Parties.Add(new ServerPerfTop.Row { Name = "Factions", LogicMs = 3.0, PhysicsMs = 4.5, Count = 10 });
            t.Grids.Add(new ServerPerfTop.Row { Name = "Small", Party = "Arkil", LogicMs = 0.1 });
            t.Grids.Add(new ServerPerfTop.Row { Name = "WHI-24Drill", Party = "Factions", LogicMs = 2.0, PhysicsMs = 3.0 });
            t.EntityGrids.Add(new ServerPerfTop.Row { Name = "(not on a grid)", LogicMs = 1.0, Count = 900 });
            t.Types.Add(new ServerPerfTop.Row { Name = "MyLargeTurret", LogicMs = 2.5, Count = 5400 });
            return t;
        }

        [Fact]
        public void NoWindowYetSaysSo()
        {
            Assert.Equal("serverperf top: no window has closed yet.", ServerPerfTop.Format(null, 10, At));
        }

        [Fact]
        public void HeaderCarriesTheWindowTotalsAndItsAge()
        {
            string s = ServerPerfTop.Format(Window(), 10, At.AddSeconds(12));
            string header = s.Split('\n')[0];
            Assert.Equal("serverperf top: window 30 s ending 20:21:27Z (12 s ago), 1800 frames; ms/frame: busy 18.50,"
                       + " physics 6.25, entity logic serial 4.00 + parallel wait 1.25 (parallel cpu 3.00),"
                       + " Factions tick 1.50, physics not charged to a grid 0.75", header);
        }

        [Fact]
        public void RowsAreSortedCostliestFirstWithTheirSplit()
        {
            string[] lines = ServerPerfTop.Format(Window(), 10, At).Split('\n');
            Assert.Equal("parties: Factions 7.50 (logic 3.00 + physics 4.50, 10 simulated grids);"
                       + " Arkil 0.30 (logic 0.25 + physics 0.05, 3 simulated grids)", lines[1]);
            Assert.Equal("grids: WHI-24Drill [Factions] 5.00 (logic 2.00 + physics 3.00);"
                       + " Small [Arkil] 0.10 (logic 0.10 + physics 0.00)", lines[2]);
            Assert.Equal("entity logic by grid: (not on a grid) 1.00 (900 calls)", lines[3]);
            Assert.Equal("entity types: MyLargeTurret 2.50 (5400 calls)", lines[4]);
            Assert.Equal("scripts: none", lines[5]);
        }

        [Fact]
        public void NLimitsEachSectionAndCountsTheRest()
        {
            string[] lines = ServerPerfTop.Format(Window(), 1, At).Split('\n');
            Assert.Equal("grids: WHI-24Drill [Factions] 5.00 (logic 2.00 + physics 3.00); +1 more", lines[2]);
        }

        [Fact]
        public void WithoutALedgerThePartyAndGridSectionsSayWhy()
        {
            ServerPerfTop t = Window();
            t.HasLedger = false;
            t.PhysicsMs = double.NaN;
            string s = ServerPerfTop.Format(t, 10, At);
            Assert.Contains("physics n/a,", s);
            Assert.DoesNotContain("not charged to a grid", s, StringComparison.Ordinal);
            Assert.Contains("\nparties/grids: no ledger this window (needs per-entity cost ON and a grid census).\n", s,
                            StringComparison.Ordinal);
            Assert.DoesNotContain("\nparties: ", s, StringComparison.Ordinal);
        }
    }
}
