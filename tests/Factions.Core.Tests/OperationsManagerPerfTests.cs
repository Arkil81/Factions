using System;
using System.Collections.Generic;
using System.Diagnostics;
using Factions.Core;
using Xunit;
using Xunit.Abstractions;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Not a precise benchmark — wall-clock timing on shared CI hardware is inherently noisy, so this
    /// asserts only a generous sanity bound (catches an accidental algorithmic regression, e.g. O(n^3)
    /// creeping back in) and prints the real numbers via <see cref="ITestOutputHelper"/> for a human to
    /// actually read. See docs/material-vectors.md's open question on whether OperationsManager's
    /// full-rescan-every-tick match needs to become delta/dirty-tracking — this is the "measure against
    /// the live world before selecting an optimization" step for that question (repo convention, see
    /// the ProductionManager hot-path project). A live-server number from real `OPS_TICK_PERF` log
    /// lines is still the one that should actually decide that question, not this synthetic scenario.
    /// </summary>
    public class OperationsManagerPerfTests
    {
        private readonly ITestOutputHelper _output;
        public OperationsManagerPerfTests(ITestOutputHelper output) { _output = output; }

        private sealed class FakePrefabCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 1_000_000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId) { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId) { return 0; }
        }

        [Fact]
        public void ManyNodesManyDemands_MatchAndDispatchStayWellUnderFrameBudget()
        {
            const int nodeCount = 200;
            const int itemKinds = 10;

            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.PrefabCatalogue = new FakePrefabCatalogue();
            var st = new FactionState { Tag = "F" };
            var rng = new Random(1);

            var nodes = new List<SpatialNode>(nodeCount);
            for (int i = 0; i < nodeCount; i++)
            {
                SpatialNode n = world.Graph.Add(new Vec3(rng.Next(-500, 500), rng.Next(-500, 500), 0), 1.0, "N" + i);
                n.OwnerFactionTag = "F";
                n.HoldStrength = 1.0;
                nodes.Add(n);
            }

            // Roughly a fifth of the network holds surplus of each item; every node demands a couple.
            for (int i = 0; i < nodeCount; i++)
            {
                for (int k = 0; k < itemKinds; k++)
                {
                    string item = "Item" + k;
                    if (i % 5 == 0)
                    {
                        nodes[i].Inventory[item] = 10_000;
                        nodes[i].ShipStock["Hauler" + i + "_" + k] = 3;
                    }
                    else if (i % 3 == 0)
                    {
                        world.Demand.Post(new DemandEntry
                        {
                            FactionTag = "F",
                            Kind       = "Item:" + item,
                            NodeId     = nodes[i].Id,
                            Quantity   = 100,
                            ExpiresUtc = world.UtcNow.AddMinutes(5),
                        });
                    }
                }
            }

            var log = new List<string>();
            var manager = new OperationsManager();
            // Resolved before the clock starts. TestSupport.Tuning() creates a temp directory, copies
            // instance/tuning_defaults.json into it and parses it, so leaving it inside the timed
            // region measured disk latency alongside the tick -- and on a box whose disk is busy the
            // I/O alone can approach the budget.
            FactionTuning tuning = TestSupport.Tuning();
            var sw = Stopwatch.StartNew();
            manager.Tick(new List<FactionState> { st }, world, tuning, log);
            sw.Stop();

            _output.WriteLine($"nodes={nodeCount} itemKinds={itemKinds} dispatched={st.PendingFleets.Count} wallMs={sw.Elapsed.TotalMilliseconds:0.00}");
            foreach (string line in log)
                if (line.Contains("OPS_TICK_PERF"))
                    _output.WriteLine(line);

            // Generous — this is a regression guard against an accidental algorithmic blowup, not a
            // real perf gate. A live deploy's OPS_TICK_PERF log lines are the number that should
            // actually inform whether delta/dirty-tracking (docs/material-vectors.md) is worth building.
            Assert.True(sw.Elapsed.TotalMilliseconds < 2000,
                $"OperationsManager.Tick took {sw.Elapsed.TotalMilliseconds:0.00}ms for {nodeCount} nodes — investigate before assuming this scales.");
        }
    }
}
