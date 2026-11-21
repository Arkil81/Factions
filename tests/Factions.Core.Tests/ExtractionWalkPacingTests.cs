using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// The NPC extraction walk's pacing and its per-pass indexes (docs/mining-plan.md, "Slice 2b as
    /// built"). The staged cycle walks an order for at most
    /// <see cref="FactionTuning.ExtractionMineAttemptsPerStep"/> Mine trips per step and resumes it on
    /// the next; the catchment and the drill search read indexes built once per pass. None of it may
    /// change what is dispatched: every test here compares against the synchronous tick or against
    /// the per-call computation it replaced, on the IGSL-shaped estate of
    /// <see cref="ExtractionWalkPerfTests"/>.
    /// </summary>
    [Collection(DebugTracesCollection.Name)]
    public sealed class ExtractionWalkPacingTests : IDisposable
    {
        public void Dispose() { DebugTraces.ResetToDefaults(); }

        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // ---- the budget ------------------------------------------------------------------------------

        /// <summary>At a budget of one, no staged step attempts more than one Mine trip, and the pass
        /// attempts exactly as many as it does unpaced. The unpaced run is the discriminator: some
        /// step of it attempts many, so the per-step assertion can fail.</summary>
        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public void AStagedStep_AttemptsAtMostTheBudget_AndThePassAttemptsTheSameTotal(int budget)
        {
            List<int> unpaced = AttemptsPerStep(0);
            List<int> paced = AttemptsPerStep(budget);

            Assert.True(unpaced.Max() > budget,
                "the unpaced walk must exceed the budget in one step, or this test cannot fail");
            Assert.All(paced, n => Assert.InRange(n, 0, budget));
            Assert.Equal(unpaced.Sum(), paced.Sum());
            Assert.True(paced.Sum() > 100, "the fixture must attempt enough trips to pace");
        }

        private static List<int> AttemptsPerStep(int budget)
        {
            FactionTuning tuning = TestSupport.Tuning();
            tuning.ExtractionMineAttemptsPerStep = budget;
            FactionState st;
            WorldModel world = ExtractionWalkPerfTests.IgslShaped(tuning, out st);
            var states = new List<FactionState> { st };
            OperationsManager mgr = ExtractionWalkPerfTests.Manager();
            var perStep = new List<int>();

            ExtractionWalkPerfTests.Invoke(mgr, "BeginSweepForTesting", states);
            var log = new List<string>();
            for (int steps = 0; ; steps++)
            {
                Assert.True(steps < 100_000, "Operations sweep did not complete");
                int before = mgr.LastTelemetry.ExtractionMineAttempts;
                bool done = (bool)ExtractionWalkPerfTests.Invoke(mgr, "StepSweepOnceForTesting", states, world, tuning, log);
                // The sweep's end hands LastTelemetry back untouched, so the delta is still readable.
                perStep.Add(mgr.LastTelemetry.ExtractionMineAttempts - before);
                if (done) break;
            }
            return perStep;
        }

        // ---- staged and paced vs the synchronous tick ------------------------------------------------

        /// <summary>Three passes with dwells resolving in between: the synchronous tick, the staged
        /// cycle unpaced, and the staged cycle paced to one and to three trips per step dispatch the
        /// same trips from the same hulls, stage the same Ship:Mine demands, write the same trace and
        /// keep the same plan.</summary>
        [Theory]
        [InlineData(500.0)]
        [InlineData(250.0)]
        public void PacedStagedCycle_DispatchesExactlyAsTheSynchronousTick(double radiusKm)
        {
            List<string> sync = Transcript(staged: false, budget: 0, radiusKm: radiusKm);
            Assert.Contains(sync, l => l.Contains("MINE_DISPATCHED") && l.Contains(" order="));
            if (radiusKm >= 500)
            {
                // The whole estate in reach: drills run out mid-pass, and the gravity band is tried.
                Assert.Contains(sync, l => l.Contains("no_route_ship_demand_staged"));
                Assert.Contains(sync, l => l.Contains("remaining=no_idle_drill"));
            }
            else
            {
                // Each hub its own catchment: the Ship:Mine fallbacks name more than one hardest seam.
                Assert.True(sync.Where(l => l.StartsWith("Ship:Mine", StringComparison.Ordinal))
                    .Select(l => l.Split(' ')[2]).Distinct().Count() > 1);
            }

            Assert.Equal(sync, Transcript(staged: true, budget: 0, radiusKm: radiusKm));
            Assert.Equal(sync, Transcript(staged: true, budget: 1, radiusKm: radiusKm));
            Assert.Equal(sync, Transcript(staged: true, budget: 3, radiusKm: radiusKm));
        }

        private static List<string> Transcript(bool staged, int budget, double radiusKm = 500)
        {
            DebugTraces.Extraction = true;
            FactionTuning tuning = TestSupport.Tuning();
            tuning.ExtractionMineAttemptsPerStep = budget;
            tuning.MineSearchRadiusKm = radiusKm;
            FactionState st;
            WorldModel world = ExtractionWalkPerfTests.IgslShaped(tuning, out st, nodes: 120, seed: 11, drillNodeCount: 8);
            var states = new List<FactionState> { st };
            OperationsManager mgr = ExtractionWalkPerfTests.Manager();
            var transcript = new List<string>();
            var inv = CultureInfo.InvariantCulture;

            for (int pass = 1; pass <= 3; pass++)
            {
                var log = new List<string>();
                if (staged) ExtractionWalkPerfTests.RunStagedSweep(mgr, states, world, log);
                else mgr.Tick(states, world, tuning, log);

                transcript.Add("-- pass " + pass);
                transcript.AddRange(log.Where(l => l.Contains("MINE") || l.StartsWith("EXTRACTION_PLAN", StringComparison.Ordinal))
                    .Select(l => Regex.Replace(l, @"^\[[0-9A-F]+\] ", "")));
                // The synchronous tick commits its plan, and so traces the ground signals, before it
                // dispatches; the staged cycle only once it commits. Same lines, another place.
                transcript.AddRange(log.Where(l => l.StartsWith("EXTRACTION_GROUND", StringComparison.Ordinal))
                    .OrderBy(l => l, StringComparer.Ordinal));
                transcript.AddRange(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine)
                    .Select(f => f.ShipPrefabId + " " + f.OriginNodeId + "->" + (f.TargetNodeId ?? "(nav)") + " "
                        + f.CurrentStopKind + " stops=" + string.Join(",", (f.RemainingStops ?? new List<FleetStop>())
                            .Select(s => s.Kind + ":" + (s.NodeId ?? "-")))
                        + " eta=" + f.EstimatedArrivalUtc.ToString("HH:mm:ss.fff", inv))
                    .OrderBy(s => s, StringComparer.Ordinal));
                transcript.AddRange(world.Demand.SnapshotShipDemands(st.Tag)
                    .Select(d => d.Kind + " " + d.NodeId + " via=" + d.ViaNodeId + " dest=" + d.DestNodeId)
                    .OrderBy(s => s, StringComparer.Ordinal));
                transcript.AddRange(mgr.ExtractionOrdersFor(st.Tag).Select(o => o.Key + " paused=" + o.Paused
                    + " bound=" + o.Bound + " target=" + o.TargetRateLitersPerHour.ToString("0", inv)));
                transcript.AddRange(world.Graph.Nodes.Values
                    .Where(n => n.MineCooldownUntilUtc > world.UtcNow)
                    .Select(n => n.Id + " cool=" + n.MineCooldownUntilUtc.ToString("HH:mm:ss", inv))
                    .OrderBy(s => s, StringComparer.Ordinal));

                world.UtcNow = world.UtcNow.AddMinutes(3);
                PendingFleetResolver.ResolveArrivals(states, world, tuning, new SeededRandom(1),
                    new List<string>(), itemVolumes: new ExtractionOrderVolumes());
            }
            return transcript;
        }

        /// <summary>The same three passes under the synchronous tick, as the code before the pass
        /// indexes and the pacing (origin/testing @ 0cf81634, v0.354.0) wrote them: a SHA-256 of the
        /// transcript, which is several hundred lines. The equivalence test above ties every paced and
        /// staged run to the synchronous tick; this ties the synchronous tick to the code it
        /// replaced, so an index that answered differently would show here even though both paths
        /// share it. At 500 km every hub's catchment is the whole estate; at 250 km each hub has its
        /// own, which is what separates a per-destination index from a shared one. On a mismatch the
        /// first lines are printed; a deliberate behaviour change recaptures the digest. The 250 km digest
        /// was recaptured when ring legs moved to cruise speed (FAC-509): one delivery ETA, N012->N010,
        /// moved 5.1 s with the ring point its dwell ended on; the line count is unchanged.</summary>
        [Theory]
        [InlineData(500.0, "caef28c048346d285af4768793d1a4b2505425e43a2b2acc8ea54fdc13694331", 823)]
        [InlineData(250.0, "ef29b9ba6cf25c67b9c431be8aa475a6e07a439171f8977677dbae7a9c84d57e", 820)]
        public void TheSynchronousTick_WritesTheTranscriptTheCodeBeforeThePassIndexesWrote(double radiusKm,
            string Before, int BeforeLines)
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                List<string> transcript = Transcript(staged: false, budget: 0, radiusKm: radiusKm);
                string digest = Digest(transcript);
                Assert.True(Before == digest && BeforeLines == transcript.Count,
                    "transcript " + transcript.Count + " lines, sha256 " + digest + "\n"
                    + string.Join("\n", transcript.Take(12)));
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }

        internal static string Digest(List<string> lines)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)));
                return string.Concat(hash.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private sealed class ExtractionOrderVolumes : IItemVolumeCatalogue
        {
            public double VolumeLitersPerUnit(string key) => key.StartsWith("Ore:", StringComparison.Ordinal) ? 0.37 : 1.0;
        }

        // ---- the per-pass catchment ------------------------------------------------------------------

        /// <summary>For every hub and ore of the estate, the pass-indexed catchment is the one
        /// <c>FindMineCatchment(hub, owned, tuning, {ore: 1}, registry, yieldingOnly: true)</c>
        /// builds per call: same members, same order. Also with a search radius that cuts the
        /// estate, so the distance filter is exercised.</summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(150.0)]
        public void ThePassCatchment_IsTheOneFindMineCatchmentBuilds(double radiusKm)
        {
            FactionTuning tuning = TestSupport.Tuning();
            tuning.MineSearchRadiusKm = radiusKm;
            FactionState st;
            WorldModel world = ExtractionWalkPerfTests.IgslShaped(tuning, out st);
            List<SpatialNode> owned = world.Graph.Nodes.Values.Where(n => n.OwnerFactionTag == st.Tag).ToList();

            Type passType = typeof(OperationsManager).GetNestedType("StandingMineScanPass", BindingFlags.NonPublic);
            object pass = Activator.CreateInstance(passType, nonPublic: true);
            MethodInfo indexed = passType.GetMethod("ExtractionCatchment", Any);
            MethodInfo perCall = typeof(OperationsManager).GetMethod("FindMineCatchment", Any);

            FactionTuning unbounded = TestSupport.Tuning();
            unbounded.MineSearchRadiusKm = 0;
            int compared = 0, nonEmpty = 0, cut = 0;
            foreach (SpatialNode hub in owned.Where((n, i) => i % 3 == 0))
                foreach (string ore in new[] { "Ice", "Iron", "Magnesium", "Stone", "Gold", "Platinum", "Cobalt" })
                {
                    string oreKey = "Ore:" + ore;
                    var weights = new Dictionary<string, double>(StringComparer.Ordinal) { { oreKey, 1.0 } };
                    var expected = (List<SpatialNode>)perCall.Invoke(null,
                        new object[] { hub, owned, tuning, weights, world.Registry, true });
                    var actual = (List<SpatialNode>)indexed.Invoke(pass,
                        new object[] { hub, oreKey, weights, owned, tuning, world.Registry });
                    Assert.Equal(expected.Select(n => n.Id), actual.Select(n => n.Id));
                    compared++;
                    if (expected.Count > 0) nonEmpty++;
                    var whole = (List<SpatialNode>)perCall.Invoke(null,
                        new object[] { hub, owned, unbounded, weights, world.Registry, true });
                    if (expected.Count > 0 && expected.Count < whole.Count) cut++;
                }
            Assert.True(nonEmpty > compared / 2, "most catchments must be non-empty for the comparison to mean anything");
            if (radiusKm > 0) Assert.True(cut > 0, "the radius must cut some catchment");
        }

        // ---- the hardest-gravity split ---------------------------------------------------------------

        /// <summary><see cref="MineTargeting.HardestGravityDeposit"/> is now its incumbent-free half
        /// (<see cref="MineTargeting.HardestInCatchment"/>, kept per destination for the pass) and a
        /// challenge. It must answer as the single loop it replaced did, ties and all: an incumbent
        /// is kept on equal gravity, and equal challengers resolve to the lowest node id.</summary>
        [Fact]
        public void HardestGravityDeposit_AnswersAsTheSingleLoopItReplaced()
        {
            FactionTuning tuning = TestSupport.Tuning();
            tuning.MineSearchRadiusKm = 120;
            var rng = new Random(3);
            int challenged = 0, kept = 0;
            for (int trial = 0; trial < 40; trial++)
            {
                WorldModel world = TestSupport.World();
                var gravity = new Dictionary<string, double>(StringComparer.Ordinal);
                var nodes = new List<SpatialNode>();
                for (int i = 0; i < 30; i++)
                {
                    SpatialNode n = world.Graph.Add(new Vec3(rng.Next(-150, 150), rng.Next(-150, 150), 0), 1.0,
                        "G" + rng.Next(1000).ToString("000", CultureInfo.InvariantCulture) + "_" + i);
                    if (rng.Next(4) > 0)
                    {
                        n.Deposit = new NodeDeposit();
                        n.Deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
                    }
                    gravity[n.Id] = rng.Next(4) * 0.25;           // few distinct values: ties are common
                    nodes.Add(n);
                }
                var planets = new TableGravity(nodes, gravity);
                Vec3 anchor = new Vec3(rng.Next(-100, 100), rng.Next(-100, 100), 0);
                double g;
                SpatialNode hardest = MineTargeting.HardestInCatchment(nodes, anchor, planets, tuning, out g);
                foreach (SpatialNode incumbent in nodes)
                {
                    SpatialNode expected = ReferenceHardest(nodes, anchor, planets, tuning, incumbent);
                    Assert.Same(expected, MineTargeting.HardestGravityDeposit(nodes, anchor, planets, tuning, incumbent));
                    Assert.Same(expected, MineTargeting.ChallengeIncumbent(hardest, g, incumbent, planets));
                    if (ReferenceEquals(expected, incumbent)) kept++; else challenged++;
                }
            }
            Assert.True(challenged > 100 && kept > 100, "both outcomes must occur: challenged=" + challenged + " kept=" + kept);
        }

        /// <summary>The fallback's hardest seam is kept per destination for the pass, so two hubs
        /// whose catchments do not overlap each name a seam of their own. Both orders find no idle
        /// drill (the faction's one drill is out working) and stage a <c>Ship:Mine</c> demand from
        /// their vacuum deposit; the seam the hull must survive is the one in gravity next to that
        /// hub, not the one next to the hub that asked first.</summary>
        [Fact]
        public void TheFallbackSeam_IsTheHardestInEachHubsOwnCatchment()
        {
            FactionTuning tuning = TestSupport.Tuning();
            tuning.MineSearchRadiusKm = 100;
            WorldModel world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
            world.Tuning = tuning;
            world.PrefabCatalogue = new ExtractionWalkPerfTests.Catalogue();
            world.Planets = new YAboveGravity(50, 0.5);
            var st = new FactionState { Tag = "IG" };

            foreach (double x in new[] { 0.0, 1000.0 })
            {
                string h = x == 0 ? "A" : "B";
                SpatialNode hub = Owned(world, h + "Hub", x, 0);
                hub.DemandHistory["Ore:Iron"] = 1e7;
                SpatialNode flat = Owned(world, h + "Flat", x + 10, 0);        // tried first: richer
                flat.Deposit = new NodeDeposit();
                flat.Deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
                flat.Richness = 1.0;
                SpatialNode steep = Owned(world, h + "Steep", x + 10, 60);    // in gravity
                steep.Deposit = new NodeDeposit();
                steep.Deposit.Entries.Add(new DepositEntry("Iron", 1.0, 100.0));
                steep.Richness = 0.5;
            }
            // Owned, so no bootstrap mint; out working, so no idle drill.
            st.PendingFleets.Add(new PendingFleetAction
            {
                Kind = ActionKind.Mine, ActorTag = "IG", ShipPrefabId = "WHI", ConvoySize = 1,
                OriginNodeId = "AFlat", TargetNodeId = "AFlat", CurrentStopKind = FleetStopKind.Mine,
                DepartedUtc = world.UtcNow, EstimatedArrivalUtc = world.UtcNow.AddMinutes(10),
                MineWeights = new Dictionary<string, double> { { "Ore:Iron", 1.0 } }, MineTargetLiters = 1_000,
                RemainingStops = new List<FleetStop> { new FleetStop { NodeId = "AHub", Kind = FleetStopKind.Service } },
            });

            ExtractionWalkPerfTests.Manager().Tick(new List<FactionState> { st }, world, tuning, new List<string>());

            List<DemandEntry> demands = world.Demand.SnapshotShipDemands("IG").Where(d => d.Kind == "Ship:Mine").ToList();
            Assert.Equal("ASteep", Assert.Single(demands, d => d.DestNodeId == "AHub").ViaNodeId);
            Assert.Equal("BSteep", Assert.Single(demands, d => d.DestNodeId == "BHub").ViaNodeId);
        }

        private static SpatialNode Owned(WorldModel world, string id, double x, double y)
        {
            SpatialNode n = world.Graph.Add(new Vec3(x, y, 0), 1.0, id);
            n.OwnerFactionTag = "IG";
            n.HoldStrength = 1.0;
            return n;
        }

        private sealed class YAboveGravity : IPlanetCatalogue
        {
            private readonly double _y, _g;
            public YAboveGravity(double y, double g) { _y = y; _g = g; }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { return posKm.Y > _y ? _g : 0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }

        /// <summary>The loop <see cref="MineTargeting.HardestGravityDeposit"/> ran before the split,
        /// verbatim.</summary>
        private static SpatialNode ReferenceHardest(IEnumerable<SpatialNode> ownedNodes, Vec3 anchorPos,
            IPlanetCatalogue planets, FactionTuning tuning, SpatialNode incumbent)
        {
            double hardestG = planets.GravityAt(incumbent.Position);
            SpatialNode hardest = null;
            foreach (SpatialNode n in ownedNodes)
            {
                if (!MineTargeting.IsInCatchment(n, anchorPos, tuning)) continue;
                double g = planets.GravityAt(n.Position);
                if (g > hardestG
                    || (hardest != null && g == hardestG && string.CompareOrdinal(n.Id, hardest.Id) < 0))
                {
                    hardest = n;
                    hardestG = g;
                }
            }
            return hardest ?? incumbent;
        }

        private sealed class TableGravity : IPlanetCatalogue
        {
            private readonly Dictionary<Vec3, double> _byPos = new Dictionary<Vec3, double>();
            public TableGravity(List<SpatialNode> nodes, Dictionary<string, double> gravity)
            {
                foreach (SpatialNode n in nodes) _byPos[n.Position] = gravity[n.Id];
            }
            public IEnumerable<PlanetRecord> Planets { get { yield break; } }
            public double GravityAt(Vec3 posKm) { double g; return _byPos.TryGetValue(posKm, out g) ? g : 0; }
            public Vec3 NearestSurfacePoint(Vec3 posKm) { return posKm; }
            public Vec3 SurfaceNormal(Vec3 posKm) { return new Vec3(0, 1, 0); }
        }
    }
}
