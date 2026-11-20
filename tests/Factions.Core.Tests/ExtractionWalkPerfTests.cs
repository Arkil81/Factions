using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Factions.Core;
using Xunit;
using Xunit.Abstractions;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The NPC extraction-order walk (docs/mining-plan.md, slice 2b) on an estate shaped like IGSL's
    /// on 2026-09-27: ~220 owned nodes, most of them deposits inside every hub's catchment, ~90 idle
    /// drills, several dozen open orders, and a band of deposits in gravity no drill can fly to, which
    /// the walk re-tries on every order that reaches it (the <c>no_route_ship_demand_staged</c> token
    /// that dominated IGSL's and IMPR's scans). Wall-clock numbers are printed, not asserted tightly:
    /// the bounds here catch an algorithmic regression, and the budget and equivalence tests are
    /// exact.
    /// </summary>
    public sealed class ExtractionWalkPerfTests
    {
        private readonly ITestOutputHelper _output;
        public ExtractionWalkPerfTests(ITestOutputHelper output) { _output = output; }

        private static readonly string[] Ores =
            { "Ice", "Iron", "Magnesium", "Stone", "Silicon", "Nickel", "Uranium", "Gold", "Platinum" };

        internal sealed class Catalogue : IPrefabCatalogue
        {
            private static readonly IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> DrillBands =
                new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>
                {
                    [ThrustDirection.Down] = new List<ThrustBand> { new ThrustBand
                    {
                        ThrusterType = "Hydrogen", ForceMagnitudeN = 100_000f,
                        MinPlanetaryInfluence = 0f, MaxPlanetaryInfluence = 0f,
                        EffectivenessAtMinInfluence = 1f, EffectivenessAtMaxInfluence = 1f,
                    }},
                };
            private static readonly IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> None =
                new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>();

            public long CargoLiters(string prefabId)
            {
                return prefabId == "WHI" ? 375_000 : prefabId.StartsWith("Hauler", StringComparison.Ordinal) ? 50_000 : 0;
            }
            public double DrillYieldScore(string prefabId) { return prefabId == "WHI" ? 128.6 : 0; }
            // A 10 t drill with 100 kN of lift: flies anywhere in vacuum, nowhere at 1.5 g.
            public double DryMassKg(string prefabId) { return prefabId == "WHI" ? 10_000 : 0; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
                => prefabId == "WHI" ? DrillBands : None;
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) => new Dictionary<string, long>();
            public long Cost(string prefabId) => 0;
            public string FactionHint(string prefabId) => "All";
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) => new WeaponBand[0];
            public string GridSize(string prefabId) => "Small";
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) => new string[0];
        }

        /// <summary>1.5 g above y = 120 km, vacuum below: the deposits up there are route-infeasible
        /// for every drill the estate owns.</summary>
        private sealed class GravityBand : IPlanetCatalogue
        {
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { return posKm.Y > 120 ? 1.5 : 0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        private sealed class Volumes : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string key) => key.StartsWith("Ore:", StringComparison.Ordinal) ? 0.37 : 1.0;
        }

        internal static OperationsManager Manager() { return new OperationsManager(itemVolumes: new Volumes()); }

        /// <summary>~220 nodes in a 400 km square (every one inside every hub's 500 km catchment),
        /// four in five of them deposits of two or three ores, 90 WHI drills parked three to a node
        /// in vacuum, a hauler on every other node, and 20 hubs drawing three ores each. The nodes in
        /// the gravity band stand on one planet, so a blocked trip between two of them also runs the
        /// local-shipyard census, as it does on a live body.</summary>
        internal static WorldModel IgslShaped(FactionTuning tuning, out FactionState st, int nodes = 220, int seed = 7,
            int drillNodeCount = 30)
        {
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            world.Tuning = tuning;
            world.PrefabCatalogue = new Catalogue();
            world.Planets = new GravityBand();
            st = new FactionState { Tag = "IG" };
            var rng = new Random(seed);

            var hubs = new List<SpatialNode>();
            int drillNodes = 0;
            for (int i = 0; i < nodes; i++)
            {
                SpatialNode n = world.Graph.Add(new Vec3(rng.Next(-200, 200), rng.Next(-200, 200), 0), 1.0,
                    "N" + i.ToString("000", CultureInfo.InvariantCulture));
                n.OwnerFactionTag = st.Tag;
                n.HoldStrength = 1.0;
                if (n.Position.Y > 120) n.PlanetId = "P1";   // the gravity band is one body's region
                if (i % 2 == 0) n.ShipStock["Hauler" + (i % 7)] = 2;

                if (i % 5 == 0)
                {
                    if (hubs.Count < 20) hubs.Add(n);
                    continue;
                }
                n.Deposit = new NodeDeposit();
                int count = 2 + rng.Next(2);
                var picked = new HashSet<string>(StringComparer.Ordinal);
                while (picked.Count < count) picked.Add(Ores[rng.Next(Ores.Length)]);
                foreach (string ore in picked.OrderBy(o => o, StringComparer.Ordinal))
                    n.Deposit.Entries.Add(new DepositEntry(ore, 1.0 / count, 100.0 / count));
                n.Richness = NodeContent.RichnessFor(n, tuning);

                if (drillNodes < drillNodeCount && n.Position.Y <= 120)
                {
                    n.ShipStock["WHI"] = 3;
                    drillNodes++;
                }
            }

            for (int h = 0; h < hubs.Count; h++)
                for (int k = 0; k < 3; k++)
                    hubs[h].DemandHistory["Ore:" + Ores[(h + k * 4) % Ores.Length]] = 1e7;
            return world;
        }

        internal static void RunStagedSweep(OperationsManager manager, IList<FactionState> states, WorldModel world,
            List<string> log, List<double> stepMs = null, List<string> stepPhase = null)
        {
            Invoke(manager, "BeginSweepForTesting", states);
            int steps = 0;
            while (true)
            {
                string phase = stepPhase != null ? CurrentPhase(manager) : null;
                long start = Stopwatch.GetTimestamp();
                bool done = (bool)Invoke(manager, "StepSweepOnceForTesting", states, world, world.Tuning, log);
                if (stepMs != null) stepMs.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
                if (stepPhase != null) stepPhase.Add(phase);
                if (done) return;
                Assert.True(++steps < 100_000, "Operations sweep did not complete");
            }
        }

        /// <summary>The phase the next step will run, read off the sweep cursor.</summary>
        private static string CurrentPhase(OperationsManager manager)
        {
            const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object sweep = typeof(OperationsManager).GetField("_sweep", Any).GetValue(manager);
            object cycle = sweep == null ? null : sweep.GetType().GetField("CurrentCycle", Any).GetValue(sweep);
            return cycle == null ? "Select" : cycle.GetType().GetField("Phase", Any).GetValue(cycle).ToString();
        }

        internal static object Invoke(object target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method.Invoke(target, arguments);
        }

        private static void AdvancePass(WorldModel world, IList<FactionState> states)
        {
            world.UtcNow = world.UtcNow.AddMinutes(3);
            PendingFleetResolver.ResolveArrivals(states, world, world.Tuning, new SeededRandom(1),
                new List<string>(), itemVolumes: new Volumes());
        }

        [Fact]
        public void IgslShapedEstate_ExtractionWalk_StaysBoundedPerStep()
        {
            FactionTuning tuning = TestSupport.Tuning();

            // Warm the JIT on a small copy so the first measured pass is not compiling.
            FactionState warm;
            WorldModel warmWorld = IgslShaped(tuning, out warm, nodes: 40);
            RunStagedSweep(Manager(), new List<FactionState> { warm }, warmWorld, new List<string>());
            Manager().Tick(new List<FactionState> { warm }, warmWorld, tuning, new List<string>());

            FactionState st;
            WorldModel world = IgslShaped(tuning, out st);
            var states = new List<FactionState> { st };
            OperationsManager mgr = Manager();
            double worstStep = 0;
            for (int pass = 1; pass <= 4; pass++)
            {
                var log = new List<string>();
                var steps = new List<double>();
                var phases = new List<string>();
                RunStagedSweep(mgr, states, world, log, steps, phases);
                double total = steps.Sum();
                double max = steps.Max();
                worstStep = Math.Max(worstStep, max);
                int dispatched = log.Count(l => l.Contains("MINE_DISPATCHED"));
                int blocked = log.Count(l => l.Contains("MINE_DISPATCH_BLOCKED"));
                _output.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "staged pass {0}: orders={1} steps={2} totalMs={3:0.00} maxStepMs={4:0.00} p50StepMs={5:0.000} dispatched={6} blocked={7}",
                    pass, mgr.ExtractionOrdersFor("IG").Count(o => !o.Paused), steps.Count, total, max,
                    steps.OrderBy(x => x).ElementAt(steps.Count / 2), dispatched, blocked));
                _output.WriteLine("  top steps: " + string.Join(" ", steps.Select((ms, i) => new { ms, i })
                    .OrderByDescending(x => x.ms).Take(4)
                    .Select(x => "#" + x.i + ":" + phases[x.i] + "=" + x.ms.ToString("0.00", CultureInfo.InvariantCulture))));
                _output.WriteLine("  maxStepMs by phase: " + string.Join(" ", steps.Select((ms, i) => new { ms, p = phases[i] })
                    .GroupBy(x => x.p).Select(g => g.Key + "=" + g.Max(x => x.ms).ToString("0.00", CultureInfo.InvariantCulture)
                        + "/" + g.Count())));
                foreach (string line in log.Where(l => l.StartsWith("OPS_TICK_PERF", StringComparison.Ordinal)))
                    _output.WriteLine("  " + line.Substring(line.IndexOf("totalMs", StringComparison.Ordinal)));
                AdvancePass(world, states);
            }

            FactionState syncSt;
            WorldModel syncWorld = IgslShaped(tuning, out syncSt);
            var syncStates = new List<FactionState> { syncSt };
            OperationsManager syncMgr = Manager();
            for (int pass = 1; pass <= 4; pass++)
            {
                var sw = Stopwatch.StartNew();
                syncMgr.Tick(syncStates, syncWorld, tuning, new List<string>());
                sw.Stop();
                _output.WriteLine(string.Format(CultureInfo.InvariantCulture, "sync pass {0}: tickMs={1:0.00}",
                    pass, sw.Elapsed.TotalMilliseconds));
                AdvancePass(syncWorld, syncStates);
            }

            Assert.True(worstStep < 2000, "one staged step took " + worstStep.ToString("0.0", CultureInfo.InvariantCulture) + " ms");
        }
    }
}
