using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>FAC-512: a spawn still waiting its turn follows its fleet, and is dropped when no
    /// player is near it any more, instead of landing where the fleet was and being closed unseen.</summary>
    public class SpawnWithdrawTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 21, 13, 0, 0, DateTimeKind.Utc);
        private const byte Main = 2;

        private static NexusFleetRegistry Flying()
        {
            var r = new NexusFleetRegistry();
            r.Apply(Main, "s1", true, null, null, null, 0, T0);
            return r;
        }

        private static NexusFleetOrder SpawnOrder(string key)
        {
            return new NexusFleetOrder { Key = key, Kind = (int)ActionKind.Haul, Tag = "UNGD", Prefab = "Wakizashi", X = 100, Tx = 150 };
        }

        private static List<NexusFleetOrder> Orders(NexusFleetRegistry r, DateTime at)
        {
            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(Main, at, out orders, out asks, out held);
            return orders ?? new List<NexusFleetOrder>();
        }

        [Fact]
        public void AWaitingSpawnIsMovedToWhereTheFleetIs_AtMostEveryTwoSeconds()
        {
            NexusFleetRegistry r = Flying();
            r.Spawn(Main, SpawnOrder("k1"), T0);

            Assert.True(r.Respot("k1", new Vec3(103, 0, 0), new Vec3(150, 0, 0), T0.AddSeconds(1)));
            // Moved again within the interval: not sent.
            Assert.False(r.Respot("k1", new Vec3(110, 0, 0), new Vec3(150, 0, 0), T0.AddSeconds(2)));
            // Past the interval but under the distance: not sent.
            Assert.False(r.Respot("k1", new Vec3(103.2, 0, 0), new Vec3(150, 0, 0), T0.AddSeconds(4)));
            Assert.True(r.Respot("k1", new Vec3(110, 0, 0), new Vec3(150, 0, 0), T0.AddSeconds(4)));

            List<NexusFleetOrder> orders = Orders(r, T0.AddSeconds(4));
            Assert.Equal(new[] { NexusFleetOp.Spawn, NexusFleetOp.Respot, NexusFleetOp.Respot }, orders.ConvertAll(o => o.Op).ToArray());
            Assert.Equal(110, orders[2].X);
            Assert.Equal(150, orders[2].Tx);
            Assert.Equal("k1", orders[2].Key);
        }

        [Fact]
        public void ARespotForAKeyNotWaiting_SendsNothing()
        {
            NexusFleetRegistry r = Flying();
            Assert.False(r.Respot("k1", new Vec3(103, 0, 0), new Vec3(150, 0, 0), T0));
            Assert.Empty(Orders(r, T0));
        }

        [Fact]
        public void AWithdrawnSpawn_IsNotTakenWhenItsGridIsReportedAfterwards()
        {
            NexusFleetRegistry r = Flying();
            r.Spawn(Main, SpawnOrder("k1"), T0);
            Assert.True(r.Withdraw("k1", T0.AddSeconds(5)));
            Assert.False(r.IsSpawning("k1"));
            Assert.False(r.Withdraw("k1", T0.AddSeconds(5)));

            List<NexusFleetOrder> orders = Orders(r, T0.AddSeconds(5));
            Assert.Equal(NexusFleetOp.Withdraw, orders[orders.Count - 1].Op);
            Assert.Equal("k1", orders[orders.Count - 1].Key);

            // It landed before the agent read the order: not ours, never handed to the fleet.
            List<string> done = r.Apply(Main, "s1", true, new[] { new NexusFleetGrid { Key = "k1", Eid = 900, X = 100 } },
                                        null, null, orders[orders.Count - 1].Seq, T0.AddSeconds(6));
            Assert.Empty(done);
            long eid;
            Assert.False(r.TryTakeSpawn("k1", out eid));
            Assert.False(r.Owns(900));
        }

        [Fact]
        public void AWithdrawnSpawn_CanBeOrderedAgain()
        {
            NexusFleetRegistry r = Flying();
            r.Spawn(Main, SpawnOrder("k1"), T0);
            r.Withdraw("k1", T0.AddSeconds(5));
            r.Spawn(Main, SpawnOrder("k1"), T0.AddSeconds(20));
            Assert.True(r.IsSpawning("k1"));
        }

        [Fact]
        public void TheRespotClock_IsDroppedWithTheSpawn()
        {
            NexusFleetRegistry r = Flying();
            r.Spawn(Main, SpawnOrder("k1"), T0);
            Assert.True(r.Respot("k1", new Vec3(103, 0, 0), new Vec3(150, 0, 0), T0.AddSeconds(1)));
            r.Withdraw("k1", T0.AddSeconds(1.5));
            r.Spawn(Main, SpawnOrder("k1"), T0.AddSeconds(1.6));
            // A fresh spawn under the same key is not held back by the old one's interval.
            Assert.True(r.Respot("k1", new Vec3(103, 0, 0), new Vec3(150, 0, 0), T0.AddSeconds(1.7)));
        }

        [Fact]
        public void AWithdrawnKey_LeavesThePacerQueue_SoTheNextIsNotHeldBehindIt()
        {
            var p = new SpawnPacer(1.0);
            double waited;
            Assert.True(p.TryBegin("first", T0, out waited));
            p.Landed("first", T0.AddSeconds(1));
            Assert.False(p.TryBegin("gone", T0.AddSeconds(1.5), out waited));   // queued, head
            Assert.False(p.TryBegin("next", T0.AddSeconds(1.5), out waited));

            Assert.True(p.Withdraw("gone"));
            Assert.Equal(1, p.QueueLength);
            // Without the withdraw, "gone" would hold the head for QueueStaleSeconds (10 s).
            Assert.True(p.TryBegin("next", T0.AddSeconds(2.1), out waited));
        }

        [Fact]
        public void ASpawnAlreadyStarted_CannotBeWithdrawn()
        {
            var p = new SpawnPacer(1.0);
            double waited;
            Assert.True(p.TryBegin("loading", T0, out waited));
            Assert.False(p.Withdraw("loading"));
            Assert.Equal("loading", p.InFlight);
        }
    }
}
