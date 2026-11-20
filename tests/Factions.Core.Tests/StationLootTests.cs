using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="StationLoot"/>: a station's container loot is rolled from the block's own
    /// table, drawn from its node's surplus rather than created, credited back on despawn less what
    /// players took, and handed back after a restart or a close that skipped the read.
    /// </summary>
    public class StationLootTests
    {
        private const string Iron = "Ingot:Iron";
        private const string Plate = "Comp:SteelPlate";
        private const string Gold = "Ore:Gold";

        private static SpatialNode Station(WorldModel w, string id = "N1")
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.6, id);
            n.OwnerFactionTag = "FCTM";
            n.StableStationId = 7;
            return n;
        }

        private static long Stock(SpatialNode n, string key)
        {
            long have;
            n.Inventory.TryGetValue(key, out have);
            return have;
        }

        private static long Held(SpatialNode n, string key)
        {
            long have = 0;
            if (n.StationLoot != null) n.StationLoot.TryGetValue(key, out have);
            return have;
        }

        private static Dictionary<string, long> Want(params object[] pairs)
        {
            var d = new Dictionary<string, long>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) d[(string)pairs[i]] = Convert.ToInt64(pairs[i + 1]);
            return d;
        }

        private static LootTableItem Row(string key, double min, double max, float freq = 1f, bool integral = false)
        {
            return new LootTableItem { Key = key, AmountMin = min, AmountMax = max, Frequency = freq, Integral = integral };
        }

        /// <summary>An IRandom that replays a fixed sequence, so a roll's every choice is pinned.</summary>
        private sealed class Script : IRandom
        {
            private readonly double[] _values;
            private int _next;
            public Script(params double[] values) { _values = values; }
            public double NextDouble() { return _values[_next++ % _values.Length]; }
        }

        // ---- rolling ----------------------------------------------------------------------------

        [Fact]
        public void Roll_PicksWithoutReplacement_UpToTheCount()
        {
            var table = new LootTable { CountMin = 5, CountMax = 5 };
            table.Items.Add(Row(Iron, 10, 10));
            table.Items.Add(Row(Plate, 3, 3, integral: true));

            Dictionary<string, long> placed = StationLoot.Roll(table, new SeededRandom(3));

            // Five picks asked, two rows exist: each row is placed once, never twice.
            Assert.Equal(2, placed.Count);
            Assert.Equal(10, placed[Iron]);
            Assert.Equal(3, placed[Plate]);
        }

        [Fact]
        public void Roll_CountUpperBoundIsExclusive_AsSEs()
        {
            // CountMin 1, CountMax 3 draws Next(1, 3): one or two picks, never three.
            var table = new LootTable { CountMin = 1, CountMax = 3 };
            table.Items.Add(Row(Iron, 1, 1));
            table.Items.Add(Row(Plate, 1, 1));
            table.Items.Add(Row(Gold, 1, 1));

            for (int seed = 1; seed <= 200; seed++)
            {
                int picks = StationLoot.Roll(table, new SeededRandom(seed)).Count;
                Assert.InRange(picks, 1, 2);
            }
        }

        [Fact]
        public void Roll_WeightsRowsByFrequency()
        {
            var table = new LootTable { CountMin = 1, CountMax = 1 };
            table.Items.Add(Row(Iron, 1, 1, freq: 1f));
            table.Items.Add(Row(Plate, 1, 1, freq: 3f));

            // Total weight 4: a draw under 1 lands on Iron, anything from 1 to 4 on Plate.
            Assert.True(StationLoot.Roll(table, new Script(0.2, 0.5)).ContainsKey(Iron));
            Assert.True(StationLoot.Roll(table, new Script(0.3, 0.5)).ContainsKey(Plate));
        }

        [Fact]
        public void Roll_IntegralRoundsUp_BulkRoundsDown()
        {
            var table = new LootTable { CountMin = 2, CountMax = 2 };
            table.Items.Add(Row(Plate, 1, 2, integral: true));
            table.Items.Add(Row(Iron, 1, 2));

            // Row pick, then amount: Plate at 1.5 -> 2, Iron at 1.5 -> 1.
            Dictionary<string, long> placed = StationLoot.Roll(table, new Script(0.0, 0.5, 0.0, 0.5));

            Assert.Equal(2, placed[Plate]);
            Assert.Equal(1, placed[Iron]);
        }

        [Fact]
        public void Roll_ASetRowPlacesTheWholeSet()
        {
            var table = new LootTable { CountMin = 1, CountMax = 1 };
            table.Items.Add(new LootTableItem { Key = "Set:Kit", Frequency = 1f, Set = "Kit", AmountMin = 1, AmountMax = 1 });
            table.Sets["Kit"] = new List<LootTableItem> { Row(Iron, 5, 5), Row(Plate, 2, 2, integral: true) };

            Dictionary<string, long> placed = StationLoot.Roll(table, new SeededRandom(1));

            Assert.Equal(5, placed[Iron]);
            Assert.Equal(2, placed[Plate]);
            Assert.False(placed.ContainsKey("Set:Kit"));
        }

        [Fact]
        public void Roll_EmptyTable_PlacesNothing()
        {
            Assert.Empty(StationLoot.Roll(new LootTable { CountMin = 3, CountMax = 6 }, new SeededRandom(1)));
            Assert.Empty(StationLoot.Roll(null, new SeededRandom(1)));
        }

        // ---- drawing ----------------------------------------------------------------------------

        [Fact]
        public void Draw_TakesFromStock_AndRecordsIt()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = Station(w);
            n.Inventory[Iron] = 500;

            Dictionary<string, long> drawn = StationLoot.Draw(w, n, Want(Iron, 200), null);

            Assert.Equal(200, drawn[Iron]);
            Assert.Equal(300, Stock(n, Iron));
            Assert.Equal(200, Held(n, Iron));
        }

        [Fact]
        public void Draw_NeverMoreThanTheNodeHas_AndNothingFromNothing()
        {
            // The whole point: a roll the faction cannot cover is not made up out of thin air.
            WorldModel w = TestSupport.World();
            SpatialNode n = Station(w);
            n.Inventory[Iron] = 40;

            Dictionary<string, long> drawn = StationLoot.Draw(w, n, Want(Iron, 200, Gold, 50), null);

            Assert.Equal(40, drawn[Iron]);
            Assert.False(drawn.ContainsKey(Gold));
            Assert.Equal(0, Stock(n, Iron));
            Assert.Equal(0, Held(n, Gold));
        }

        [Fact]
        public void Draw_LeavesAnActiveBuildsClaimAlone()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = Station(w);
            n.Inventory[Plate] = 300;
            var build = new BuildQueueEntry { Id = "B1", Role = "Raid", ExpiresUtc = w.UtcNow.AddDays(1) };
            build.Bom[Plate] = 250;
            n.BuildQueue.Add(build);

            Dictionary<string, long> drawn = StationLoot.Draw(w, n, Want(Plate, 200), null);

            Assert.Equal(50, drawn[Plate]);
            Assert.Equal(250, Stock(n, Plate));
        }

        [Fact]
        public void ReturnUnplaced_GivesBackWhatFoundNoRoom()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = Station(w);
            n.Inventory[Iron] = 500;
            StationLoot.Draw(w, n, Want(Iron, 200), null);

            long back = StationLoot.ReturnUnplaced(w, n, Want(Iron, 200));

            Assert.Equal(200, back);
            Assert.Equal(500, Stock(n, Iron));
            Assert.Null(n.StationLoot);
        }

        // ---- crediting --------------------------------------------------------------------------

        [Fact]
        public void Credit_WhatPlayersTookIsGone_TheRestReturns()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = Station(w);
            n.Inventory[Iron] = 500;
            n.Inventory[Plate] = 30;
            StationLoot.Draw(w, n, Want(Iron, 200, Plate, 30), null);

            long taken;
            long credited = StationLoot.Credit(w, n, Want(Iron, 120), out taken);

            Assert.Equal(120, credited);
            Assert.Equal(110, taken);           // 80 iron and all 30 plate carried off
            Assert.Equal(420, Stock(n, Iron));
            Assert.Equal(0, Stock(n, Plate));
            Assert.Null(n.StationLoot);
        }

        [Fact]
        public void Credit_NeverMoreThanWasDrawn()
        {
            // A player who drops their own iron into a loot crate does not mint it into faction stock.
            WorldModel w = TestSupport.World();
            SpatialNode n = Station(w);
            n.Inventory[Iron] = 200;
            StationLoot.Draw(w, n, Want(Iron, 200), null);

            long taken;
            long credited = StationLoot.Credit(w, n, Want(Iron, 9000, Gold, 50), out taken);

            Assert.Equal(200, credited);
            Assert.Equal(0, taken);
            Assert.Equal(200, Stock(n, Iron));
            Assert.Equal(0, Stock(n, Gold));
        }

        // ---- restock cooldown -------------------------------------------------------------------

        [Fact]
        public void Credit_AnythingTaken_StartsTheRestockCooldown()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = Station(w);
            n.Inventory[Iron] = 200;
            StationLoot.Draw(w, n, Want(Iron, 200), null);

            long taken;
            StationLoot.Credit(w, n, Want(Iron, 199), out taken);

            Assert.Equal(1, taken);
            Assert.Equal(w.UtcNow + StationLoot.RestockCooldown, n.LootRestockUtc);
            TimeSpan left;
            Assert.True(StationLoot.Restocking(n, w.UtcNow.AddMinutes(59), out left));
            Assert.Equal(TimeSpan.FromMinutes(1), left);
            Assert.False(StationLoot.Restocking(n, w.UtcNow.AddHours(1), out left));
        }

        [Fact]
        public void Credit_NothingTaken_LeavesTheStoresOpen()
        {
            // A visit that took nothing must not cost the next visitor their loot.
            WorldModel w = TestSupport.World();
            SpatialNode n = Station(w);
            n.Inventory[Iron] = 200;
            StationLoot.Draw(w, n, Want(Iron, 200), null);

            long taken;
            StationLoot.Credit(w, n, Want(Iron, 200), out taken);

            Assert.Equal(0, taken);
            Assert.Equal(default(DateTime), n.LootRestockUtc);
            TimeSpan left;
            Assert.False(StationLoot.Restocking(n, w.UtcNow, out left));
        }

        [Fact]
        public void SpawnNotice_NamesTheCountdown_TheStock_OrNothing()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = Station(w);

            Assert.Equal("Depot: stores are stocked.", StationLoot.SpawnNotice("Depot", n, w.UtcNow, 40));
            Assert.Null(StationLoot.SpawnNotice("Depot", n, w.UtcNow, 0));

            n.LootRestockUtc = w.UtcNow.AddMinutes(41.5);
            Assert.Equal("Depot: stores were raided. They restock in 42 minutes.", StationLoot.SpawnNotice("Depot", n, w.UtcNow, 0));
            n.LootRestockUtc = w.UtcNow.AddSeconds(10);
            Assert.Equal("N1: stores were raided. They restock in 1 minute.", StationLoot.SpawnNotice(null, n, w.UtcNow, 0));
        }

        [Fact]
        public void CreditStale_HandsBackARecordNoReadClosed()
        {
            WorldModel w = TestSupport.World();
            SpatialNode n = Station(w);
            n.Inventory[Iron] = 200;
            StationLoot.Draw(w, n, Want(Iron, 150), null);

            Assert.Equal(150, StationLoot.CreditStale(w, n));
            Assert.Equal(200, Stock(n, Iron));
            Assert.Null(n.StationLoot);
        }

        [Fact]
        public void CreditAfterRestart_HandsEveryRecordBack()
        {
            WorldModel w = TestSupport.World();
            SpatialNode a = Station(w, "N1");
            SpatialNode b = Station(w, "N2");
            Station(w, "N3");
            a.Inventory[Iron] = 100;
            b.Inventory[Plate] = 40;
            StationLoot.Draw(w, a, Want(Iron, 100), null);
            StationLoot.Draw(w, b, Want(Plate, 25), null);

            int nodes;
            long credited = StationLoot.CreditAfterRestart(w, out nodes);

            Assert.Equal(125, credited);
            Assert.Equal(2, nodes);
            Assert.Equal(100, Stock(a, Iron));
            Assert.Equal(40, Stock(b, Plate));
            Assert.True(w.Graph.Nodes.Values.All(x => x.StationLoot == null));
        }

        [Fact]
        public void Record_RoundTripsThroughTheBrainFile_AndIsOmittedWhenEmpty()
        {
            var graph = new SpatialGraph();
            SpatialNode held = graph.Add(new Vec3(0, 0, 0), 0.6, "H1");
            held.OwnerFactionTag = "FCTM";
            held.StationLoot = new Dictionary<string, long>(StringComparer.Ordinal) { { Iron, 75 } };
            held.LootRestockUtc = new DateTime(2026, 10, 9, 21, 0, 0, DateTimeKind.Utc);
            SpatialNode empty = graph.Add(new Vec3(5, 0, 0), 0.6, "H2");
            empty.OwnerFactionTag = "FCTM";

            var st = new FactionState { Tag = "FCTM", FactionId = 9, PersonalityId = "factorum" };
            string dir = Path.Combine(Path.GetTempPath(), "factions-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new FactionStore(dir);
                store.SaveBrain(FactionStore.ToBrainDoc(st, graph.OwnedBy("FCTM"), FactionStore.DefaultLogCap));
                string json = File.ReadAllText(Directory.GetFiles(dir, "FCTM*.json", SearchOption.AllDirectories)[0]);
                Assert.Equal(1, CountOccurrences(json, "\"StationLoot\""));
                Assert.Equal(1, CountOccurrences(json, "\"LootRestockUtc\""));

                var graph2 = new SpatialGraph();
                FactionStore.ApplyBrainDoc(store.LoadBrain("FCTM"), graph2);
                Assert.Equal(75, graph2.Nodes["H1"].StationLoot[Iron]);
                Assert.Equal(held.LootRestockUtc, graph2.Nodes["H1"].LootRestockUtc.ToUniversalTime());
                Assert.Null(graph2.Nodes["H2"].StationLoot);
                Assert.Equal(default(DateTime), graph2.Nodes["H2"].LootRestockUtc);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
