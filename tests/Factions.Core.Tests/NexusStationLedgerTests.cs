using System;
using System.Collections.Generic;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>
    /// Covers <see cref="NexusStationLedger"/>: a station standing on a Nexus agent draws its
    /// magazines and loot from the authority's stock through numbered grants, nothing is drawn while
    /// a grant is unloaded, every report can be applied twice, and a restart on either side hands
    /// back what the gone grids held.
    /// </summary>
    public class NexusStationLedgerTests
    {
        private const string Nato = "NATO_25x184mm";
        private const string Iron = "Ingot:Iron";
        private const byte Day = 4;
        private static readonly TimeSpan TopUp = TimeSpan.FromSeconds(30);
        private static readonly DateTime T0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        private static WorldModel World()
        {
            WorldModel w = TestSupport.World();
            w.Tuning.MunitionDrawMode = MunitionDrawModes.Draw;
            return w;
        }

        /// <summary>A station whose design has no munition data, so its load is the agent's read
        /// capacity (40 NATO).</summary>
        private static SpatialNode Station(WorldModel w, long nato = 100, long iron = 0)
        {
            SpatialNode n = w.Graph.Add(new Vec3(0, 0, 0), 0.6, "N1");
            n.OwnerFactionTag = "RVN";
            n.StableStationId = 5;
            if (nato > 0) n.Inventory[Nato] = nato;
            if (iron > 0) n.Inventory[Iron] = iron;
            return n;
        }

        private static Dictionary<string, long> D(params object[] pairs)
        {
            var d = new Dictionary<string, long>(StringComparer.Ordinal);
            for (int i = 0; i < pairs.Length; i += 2) d[(string)pairs[i]] = Convert.ToInt64(pairs[i + 1]);
            return d;
        }

        private static long Get(Dictionary<string, long> d, string key)
        {
            long v = 0;
            if (d != null) d.TryGetValue(key, out v);
            return v;
        }

        private static NexusStationState Stood(long stand = 1)
        {
            return new NexusStationState { Node = "N1", Stand = stand, AmmoCap = D(Nato, 40), AmmoSeen = D() };
        }

        private static NexusStationReply Apply(NexusStationLedger l, WorldModel w, DateTime now, params NexusStationState[] states)
        {
            return Apply(l, w, now, "s1", states);
        }

        private static NexusStationReply Apply(NexusStationLedger l, WorldModel w, DateTime now, string session, params NexusStationState[] states)
        {
            NexusStationTally tally;
            return l.Apply(Day, session, states, w, true, true, TopUp, now, out tally);
        }

        [Fact]
        public void Stand_DrawsOneGrant_AndResendsItUntilLoaded()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            var l = new NexusStationLedger();
            NexusStationState s = Stood();

            NexusStationReply first = Apply(l, w, T0, s);
            NexusStationReply again = Apply(l, w, T0.AddMinutes(5), s);

            Assert.Single(first.Grants);
            Assert.Equal(40, Get(first.Grants[0].Ammo, Nato));
            Assert.Equal(first.Grants[0].AmmoGrant, again.Grants[0].AmmoGrant);
            // Drawn once, even with the top-up interval long past: the grant is still unloaded.
            Assert.Equal(60, n.Inventory[Nato]);
            Assert.Equal(40, Get(n.StationMunitions, Nato));
        }

        [Fact]
        public void Loaded_ReturnsUnplacedOnce_AndStopsResending()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            var l = new NexusStationLedger();
            NexusStationState s = Stood();
            long grant = Apply(l, w, T0, s).Grants[0].AmmoGrant;

            s.AmmoThrough = grant;
            s.AmmoUnplaced = D(Nato, 10);
            s.AmmoSeen = D(Nato, 30);
            NexusStationReply r1 = Apply(l, w, T0.AddSeconds(1), s);
            NexusStationReply r2 = Apply(l, w, T0.AddSeconds(2), s);

            Assert.True(r1.IsEmpty);
            Assert.True(r2.IsEmpty);
            Assert.Equal(70, n.Inventory[Nato]);
            Assert.Equal(30, Get(n.StationMunitions, Nato));
        }

        [Fact]
        public void TopUp_BooksTheShortfallSpent_AndDrawsTheGap_OnlyAfterTheInterval()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            var l = new NexusStationLedger();
            NexusStationState s = Stood();
            s.AmmoThrough = Apply(l, w, T0, s).Grants[0].AmmoGrant;
            s.AmmoSeen = D(Nato, 40);
            Apply(l, w, T0.AddSeconds(1), s);

            s.AmmoSeen = D(Nato, 25);
            Assert.True(Apply(l, w, T0.AddSeconds(10), s).IsEmpty);
            NexusStationReply topUp = Apply(l, w, T0.AddSeconds(31), s);

            Assert.Equal(15, Get(topUp.Grants[0].Ammo, Nato));
            Assert.Equal(45, n.Inventory[Nato]);
            Assert.Equal(40, Get(n.StationMunitions, Nato));
        }

        [Fact]
        public void Close_ReturnsAnUnloadedGrant_CreditsTheRest_AndIsAckedEveryTime()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            var l = new NexusStationLedger();
            NexusStationState s = Stood();
            Apply(l, w, T0, s);

            // Closed before the grant arrived: nothing was ever aboard.
            s.Closed = true;
            NexusStationReply r1 = Apply(l, w, T0.AddSeconds(1), s);
            NexusStationReply r2 = Apply(l, w, T0.AddSeconds(2), s);

            Assert.True(r1.Grants[0].CloseAcked);
            Assert.True(r2.Grants[0].CloseAcked);
            Assert.Equal(100, n.Inventory[Nato]);
            Assert.Null(n.StationMunitions);
            Assert.Equal(0, l.Count);
        }

        [Fact]
        public void Close_AfterFiring_CreditsOnlyWhatIsAboard()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            var l = new NexusStationLedger();
            NexusStationState s = Stood();
            s.AmmoThrough = Apply(l, w, T0, s).Grants[0].AmmoGrant;
            s.AmmoSeen = D(Nato, 40);
            Apply(l, w, T0.AddSeconds(1), s);

            s.Closed = true;
            s.AmmoSeen = D(Nato, 12);
            Apply(l, w, T0.AddSeconds(5), s);

            Assert.Equal(72, n.Inventory[Nato]);
        }

        [Fact]
        public void AgentRestart_CreditsEveryStationItHeld()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            var l = new NexusStationLedger();
            NexusStationState s = Stood();
            s.AmmoThrough = Apply(l, w, T0, "s1", s).Grants[0].AmmoGrant;
            s.AmmoSeen = D(Nato, 40);
            Apply(l, w, T0.AddSeconds(1), "s1", s);

            NexusStationTally tally;
            l.Apply(Day, "s2", new NexusStationState[0], w, true, true, TopUp, T0.AddSeconds(60), out tally);

            Assert.Equal(1, tally.Stranded);
            Assert.Equal(100, n.Inventory[Nato]);
            Assert.Equal(0, l.CountFor(Day));
        }

        [Fact]
        public void AuthorityRestart_DrawsOnlyTheGap_AndIgnoresTheForgottenGrantsUnplaced()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, nato: 60);
            var l = new NexusStationLedger();   // a fresh ledger: the authority restarted
            var s = new NexusStationState
            {
                Node = "N1", Stand = 1, AmmoCap = D(Nato, 40),
                AmmoThrough = 123, AmmoUnplaced = D(Nato, 5), AmmoSeen = D(Nato, 30),
            };

            NexusStationReply r = Apply(l, w, T0, s);

            Assert.Equal(10, Get(r.Grants[0].Ammo, Nato));
            Assert.NotEqual(123, r.Grants[0].AmmoGrant);
            Assert.Equal(50, n.Inventory[Nato]);
        }

        [Fact]
        public void ReStand_UnderANewId_StrandsTheOldStanding()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            var l = new NexusStationLedger();
            NexusStationState old = Stood(1);
            old.AmmoThrough = Apply(l, w, T0, old).Grants[0].AmmoGrant;
            old.AmmoSeen = D(Nato, 40);
            Apply(l, w, T0.AddSeconds(1), old);

            NexusStationReply r = Apply(l, w, T0.AddSeconds(2), Stood(2));

            // The old 40 came back, and the new standing drew a fresh 40.
            Assert.Equal(40, Get(r.Grants[0].Ammo, Nato));
            Assert.Equal(60, n.Inventory[Nato]);
            Assert.Equal(1, l.Count);
        }

        [Fact]
        public void Loot_DrawsPerBlock_ReturnsUnplaced_AndClosesWithRestockOnATake()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, nato: 0, iron: 500);
            var l = new NexusStationLedger();
            var s = new NexusStationState { Node = "N1", Stand = 1, LootWants = new List<Dictionary<string, long>> { D(Iron, 100), D(Iron, 50) } };

            NexusStationGrant g = Apply(l, w, T0, s).Grants[0];
            Assert.Equal(2, g.Loot.Count);
            Assert.Equal(100, Get(g.Loot[0], Iron));
            Assert.Equal(50, Get(g.Loot[1], Iron));

            s.LootWants = null;
            s.LootThrough = g.LootGrant;
            s.LootUnplaced = D(Iron, 20);
            Apply(l, w, T0.AddSeconds(1), s);
            Assert.Equal(370, n.Inventory[Iron]);

            s.Closed = true;
            s.LootSeen = D(Iron, 100);   // 30 taken
            Apply(l, w, T0.AddSeconds(2), s);
            Assert.Equal(470, n.Inventory[Iron]);
            Assert.True(n.LootRestockUtc > w.UtcNow);
        }

        [Fact]
        public void Loot_WhileRestocking_GrantsNothing()
        {
            WorldModel w = World();
            SpatialNode n = Station(w, nato: 0, iron: 500);
            n.LootRestockUtc = T0.AddMinutes(30);
            var l = new NexusStationLedger();
            var s = new NexusStationState { Node = "N1", Stand = 1, LootWants = new List<Dictionary<string, long>> { D(Iron, 100) } };

            NexusStationGrant g = Apply(l, w, T0, s).Grants[0];

            Assert.Empty(g.Loot[0]);
            Assert.Equal(500, n.Inventory[Iron]);
        }

        [Fact]
        public void AStationStoodWithVanillaGuns_IsNeverDrawnFor()
        {
            WorldModel w = World();
            SpatialNode n = Station(w);
            var l = new NexusStationLedger();
            var s = new NexusStationState { Node = "N1", Stand = 1 };   // no AmmoCap: not stripped

            Assert.True(Apply(l, w, T0, s).IsEmpty);
            Assert.Equal(100, n.Inventory[Nato]);
        }

        [Fact]
        public void Reply_RoundTrips_AndRejectsGarbage()
        {
            var reply = new NexusStationReply();
            reply.Grants.Add(new NexusStationGrant { Node = "N1", Stand = 9, AmmoGrant = 3, Ammo = D(Nato, 4), Loot = new List<Dictionary<string, long>> { D(), D(Iron, 2) } });
            NexusStationReply back;

            Assert.True(NexusStationReply.TryDecode(reply.Encode(), out back));
            Assert.Equal(4, Get(back.Grants[0].Ammo, Nato));
            Assert.Equal(2, back.Grants[0].Loot.Count);
            Assert.Equal(2, Get(back.Grants[0].Loot[1], Iron));
            Assert.False(NexusStationReply.TryDecode(new byte[] { 1, 2, 3 }, out back));
        }
    }
}
