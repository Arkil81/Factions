using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>MINE_AUTO_SCAN observability: every standing auto-mine order scan emits one compact
    /// per-hub summary line, but only when something dispatched or the summary's content changed --
    /// a steady state (deposits cooling down, waiting on a ship) stays silent instead of spamming
    /// once per period.</summary>
    public class AutoMineScanLoggingTests
    {
        private sealed class MinerPrefabCatalogue : IPrefabCatalogue
        {
            public long CargoLiters(string prefabId) { return 1_000_000; }
            public IReadOnlyDictionary<string, long> GasTankLiters(string prefabId) { return new Dictionary<string, long>(); }
            public long Cost(string prefabId) { return 0; }
            public string FactionHint(string prefabId) { return "All"; }
            public IReadOnlyList<WeaponBand> WeaponBands(string prefabId) { return new WeaponBand[0]; }
            public string GridSize(string prefabId) { return "Small"; }
            public IReadOnlyList<string> GetNamesForRole(string role, string factionHint) { return new string[0]; }
            public IReadOnlyDictionary<ThrustDirection, IReadOnlyList<ThrustBand>> ThrustBands(string prefabId)
                { return new Dictionary<ThrustDirection, IReadOnlyList<ThrustBand>>(); }
            public double DryMassKg(string prefabId) { return 0; }
            public double DrillYieldScore(string prefabId)
            {
                return !string.IsNullOrEmpty(prefabId) && prefabId.StartsWith("Miner", StringComparison.Ordinal) ? 1000 : 0;
            }
        }

        private static WorldModel MakeWorld()
        {
            var world = TestSupport.World();
            world.UtcNow = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            world.PrefabCatalogue = new MinerPrefabCatalogue();
            return world;
        }

        private static SpatialNode AddNode(WorldModel world, string id, string tag = "F")
        {
            SpatialNode n = world.Graph.Add(new Vec3(0, 0, 0), 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength    = 1.0;
            return n;
        }

        private static SpatialNode AddOreField(WorldModel world, string id, string tag = "F")
        {
            SpatialNode n = AddNode(world, id, tag);
            n.Richness = 1.0;   // ore under it AND measured: the mint and the drill read this
            n.Deposit = GroundHolding("Iron");
            return n;
        }

        /// <summary>A deposit holding the named ores in equal share -- what makes a node mineable now
        /// that the kind label does not confer it. See FAC-102.</summary>
        private static NodeDeposit GroundHolding(params string[] ores)
        {
            var deposit = new NodeDeposit();
            foreach (string ore in ores)
                deposit.Entries.Add(new DepositEntry(ore, 1.0 / ores.Length, 100.0 / ores.Length));
            return deposit;
        }

        private static FactionState StateWithOrder(WorldModel world, string hubNodeId, double periodSeconds = 60)
        {
            var st = new FactionState { Tag = "F" };
            StandingOrderFixtures.AddMineOrder(st, hubNodeId, periodSeconds, world.UtcNow);
            return st;
        }

        private static List<string> Tick(WorldModel world, FactionState st)
        {
            var log = new List<string>();
            new OperationsManager().Tick(new List<FactionState> { st }, world, TestSupport.Tuning(), log);
            return log;
        }

        private static IEnumerable<string> ScanLines(IEnumerable<string> log)
            => log.Where(l => l.Contains("MINE_AUTO_SCAN"));

        [Fact]
        public void CooldownGatedScan_LogsSummaryOnce_ThenSilentWhileUnchanged()
        {
            var world = MakeWorld();
            AddNode(world, "Hub");
            SpatialNode dep = AddOreField(world, "Dep");
            dep.MineCooldownUntilUtc = world.UtcNow.AddMinutes(30);
            FactionState st = StateWithOrder(world, "Hub");

            string line = Assert.Single(ScanLines(Tick(world, st)));
            Assert.Contains("hub=Hub", line);
            Assert.Contains("catchment=1", line);
            Assert.Contains("dispatched=0", line);
            Assert.Contains("Dep:cooldown_until=", line);

            // Second due scan, nothing changed -- the identical summary must not re-log.
            world.UtcNow = world.UtcNow.AddSeconds(61);
            Assert.Empty(ScanLines(Tick(world, st)));
        }

        [Fact]
        public void Scan_LogsAgainWhenOutcomeChanges_AndReportsDispatch()
        {
            var world = MakeWorld();
            AddNode(world, "Hub");
            SpatialNode dep = AddOreField(world, "Dep");
            dep.MineCooldownUntilUtc = world.UtcNow.AddSeconds(30);
            FactionState st = StateWithOrder(world, "Hub");

            Assert.Single(ScanLines(Tick(world, st))); // cooldown summary

            // Cooldown clears and a drill ship appears -- the outcome changes, so the scan re-logs.
            world.UtcNow = world.UtcNow.AddSeconds(61);
            dep.ShipStock["Miner"] = 1;
            string line = Assert.Single(ScanLines(Tick(world, st)));
            Assert.Contains("dispatched=1", line);
            Assert.Contains("Dep:dispatched", line);
            Assert.Single(st.PendingFleets.Where(f => f.Kind == ActionKind.Mine));
        }

        [Fact]
        public void EmptyCatchment_LogsOnce_ThenSilent()
        {
            var world = MakeWorld();
            AddNode(world, "Hub"); // no mineable node anywhere
            FactionState st = StateWithOrder(world, "Hub");

            string line = Assert.Single(ScanLines(Tick(world, st)));
            Assert.Contains("catchment=0", line);

            world.UtcNow = world.UtcNow.AddSeconds(61);
            Assert.Empty(ScanLines(Tick(world, st)));
        }

        [Fact]
        public void HubUnowned_LogsHubUnavailableOnce()
        {
            var world = MakeWorld();
            AddNode(world, "Hub", tag: "RIVAL");
            AddOreField(world, "Dep"); // owned deposit exists; the hub gate must fire first
            FactionState st = StateWithOrder(world, "Hub");

            string line = Assert.Single(ScanLines(Tick(world, st)));
            Assert.Contains("hub_unavailable", line);

            world.UtcNow = world.UtcNow.AddSeconds(61);
            Assert.Empty(ScanLines(Tick(world, st)));
        }

        [Fact]
        public void NoShipAnywhere_ReportsDemandStagedToken()
        {
            var world = MakeWorld();
            AddNode(world, "Hub");
            AddOreField(world, "Dep"); // off cooldown, but no drill ship in the network
            FactionState st = StateWithOrder(world, "Hub");

            string line = Assert.Single(ScanLines(Tick(world, st)));
            Assert.Contains("Dep:no_ship_demand_staged", line);
            Assert.Contains(world.Demand.SnapshotShipDemands("F"), d => d.Kind == "Ship:Mine");
        }

        // ---- FAC-056: the catchment is ordered best-first, but never filtered ------------------

        private static SpatialNode AddMineNodeAt(WorldModel world, string id, Vec3 pos, string tag = "F", params string[] groundOres)
        {
            SpatialNode n = world.Graph.Add(pos, 1.0, id);
            n.OwnerFactionTag = tag;
            n.HoldStrength    = 1.0;
            n.Richness        = 1.0;   // ore under it AND measured: the mint and the drill read this
            n.Deposit = groundOres != null && groundOres.Length > 0
                ? GroundHolding(groundOres)
                : GroundHolding("Iron");
            return n;
        }

        [Fact]
        public void StandingMineScan_WeightedOrder_ReachesTheGroundThatHasIt_First()
        {
            var world = MakeWorld();
            AddNode(world, "Hub");
            AddMineNodeAt(world, "NearOre", new Vec3(10, 0, 0),     "F", "Iron");
            AddMineNodeAt(world, "FarRare", new Vec3(200, 0, 0), "F", "Gold");

            var st = new FactionState { Tag = "F" };
            StandingOrderFixtures.AddMineOrder(st, "Hub", 60, world.UtcNow,
                oreWeights: new Dictionary<string, double> { ["Ore:Gold"] = 1.0 });

            string scan = ScanLines(Tick(world, st)).Single();

            // The scan walks the catchment in order and stops when the hub's open slots run out, so
            // the order is what decides which deposits get the scarce fleets. An order asking for
            // gold has to reach the node whose GROUND holds gold before the nearer one that holds
            // only iron -- the node kind no longer carries that signal, the deposit does.
            Assert.True(scan.IndexOf("FarRare", StringComparison.Ordinal)
                      < scan.IndexOf("NearOre", StringComparison.Ordinal), scan);
        }

        [Fact]
        public void StandingMineScan_OrderWeightsDoNotChangeCatchmentSize()
        {
            // Membership is the hub's concurrency budget ("never more simultaneous fleets than there
            // are distinct deposits", FAC-046). Ranking must not double as a filter: a poorly-scoring
            // deposit still counts, it just goes last.
            var world = MakeWorld();
            AddNode(world, "Hub");
            AddMineNodeAt(world, "NearOre", new Vec3(10, 0, 0));
            AddMineNodeAt(world, "FarRare", new Vec3(200, 0, 0));

            var unweighted = new FactionState { Tag = "F" };
            StandingOrderFixtures.AddMineOrder(unweighted, "Hub", 60, world.UtcNow);
            Assert.Contains("catchment=2", ScanLines(Tick(world, unweighted)).Single());

            var world2 = MakeWorld();
            AddNode(world2, "Hub");
            AddMineNodeAt(world2, "NearOre", new Vec3(10, 0, 0));
            AddMineNodeAt(world2, "FarRare", new Vec3(200, 0, 0));

            var weighted = new FactionState { Tag = "F" };
            StandingOrderFixtures.AddMineOrder(weighted, "Hub", 60, world2.UtcNow,
                oreWeights: new Dictionary<string, double> { ["Ore:Gold"] = 1.0 });
            Assert.Contains("catchment=2", ScanLines(Tick(world2, weighted)).Single());
        }

    }
}
