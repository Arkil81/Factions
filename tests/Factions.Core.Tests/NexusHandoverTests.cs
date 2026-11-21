using System;
using System.Collections.Generic;
using System.Linq;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>A fleet grid Nexus carries across a sector boundary: closed on one server, spawned on
    /// another with the same id and name. The registry holds it in transit and moves it to the server
    /// that reports it, rather than reading it as lost.</summary>
    public class NexusHandoverTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        private const byte Main = 2, Day = 4;

        private static NexusFleetGrid Row(string key, long eid, DateTime damaged = default(DateTime))
        {
            return new NexusFleetGrid { Key = key, Eid = eid, X = 1, Owner = "DEF", Damaged = damaged == default(DateTime) ? DateTime.MinValue : damaged };
        }

        private static NexusAgentReport Report(string session, IEnumerable<NexusFleetGrid> grids, IEnumerable<long> handedOff = null)
        {
            return new NexusAgentReport
            {
                Session = session, Fleets = true, OrdersThrough = 0,
                Grids = grids != null ? grids.ToList() : null,
                HandedOff = handedOff != null ? handedOff.ToList() : null,
            };
        }

        /// <summary>Main flies "k" as grid 7.</summary>
        private static NexusFleetRegistry FlyingOnMain(DateTime now)
        {
            var r = new NexusFleetRegistry();
            r.Apply(Main, Report("m", null), now);
            r.Apply(Day, Report("d", null), now);
            r.Spawn(Main, new NexusFleetOrder
            {
                Key = "k", Kind = (int)ActionKind.Raid, Tag = "DEF", Prefab = "Gunboat", MunitionsId = "mun-1",
            }, now);
            r.Apply(Main, Report("m", new[] { Row("k", 7) }), now);
            long eid;
            Assert.True(r.TryTakeSpawn("k", out eid));
            return r;
        }

        private static List<NexusFleetOrder> OrdersFor(NexusFleetRegistry r, byte home, DateTime now)
        {
            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(home, now, out orders, out asks, out held);
            return orders ?? new List<NexusFleetOrder>();
        }

        private static List<long> HeldBy(NexusFleetRegistry r, byte home, DateTime now)
        {
            List<NexusFleetOrder> orders; List<NexusFleetAsk> asks; List<long> held;
            r.FillReply(home, now, out orders, out asks, out held);
            return held ?? new List<long>();
        }

        /// <summary>The failure this exists for: the source's report drops the grid, which used to
        /// read as gone (and, if it was shot at, as destroyed). Handed off, it is in transit.</summary>
        [Fact]
        public void AGridHandedOff_StaysInTransit_NotGone()
        {
            NexusFleetRegistry r = FlyingOnMain(T0);
            r.Apply(Main, Report("m", new NexusFleetGrid[0], new long[] { 7 }), T0.AddSeconds(1));

            Assert.True(r.Exists(7));
            Assert.True(r.InTransit(7));
            Assert.False(r.ClosedByUs(7));
            Assert.DoesNotContain(7L, HeldBy(r, Main, T0.AddSeconds(1)));
        }

        [Fact]
        public void AGridMissingWithoutAHandOff_IsGoneAtOnce()
        {
            NexusFleetRegistry r = FlyingOnMain(T0);
            r.Apply(Main, Report("m", new NexusFleetGrid[0]), T0.AddSeconds(1));
            Assert.False(r.Exists(7));
        }

        [Fact]
        public void TheReceivingServer_TakesTheGrid_AndIsToldWhatToAdoptItAs()
        {
            NexusFleetRegistry r = FlyingOnMain(T0);
            r.Apply(Main, Report("m", new NexusFleetGrid[0], new long[] { 7 }), T0.AddSeconds(1));
            r.TakeRehomed();
            r.Apply(Day, Report("d", new[] { Row("k", 7) }), T0.AddSeconds(2));

            Assert.True(r.Exists(7));
            Assert.False(r.InTransit(7));
            Assert.Equal(new long[] { 7 }, r.TakeRehomed());
            Assert.Empty(r.TakeRehomed());
            Assert.Equal(new long[] { 7 }, HeldBy(r, Day, T0.AddSeconds(2)));
            NexusFleetOrder adopt = OrdersFor(r, Day, T0.AddSeconds(2)).Single(o => o.Op == NexusFleetOp.Adopt);
            Assert.Equal("k", adopt.Key);
            Assert.Equal(7, adopt.Eid);
            Assert.Equal((int)ActionKind.Raid, adopt.Kind);
            Assert.Equal("DEF", adopt.Tag);
            Assert.Equal("Gunboat", adopt.Prefab);
            Assert.Equal("mun-1", adopt.MunitionsId);

            // Orders now go to Day, and Main's later reports no longer speak for it.
            Assert.True(r.Retarget(7, new Vec3(9, 9, 9), T0.AddSeconds(3)));
            Assert.Contains(OrdersFor(r, Day, T0.AddSeconds(3)), o => o.Op == NexusFleetOp.Retarget && o.Eid == 7);
            Assert.DoesNotContain(OrdersFor(r, Main, T0.AddSeconds(3)), o => o.Op == NexusFleetOp.Retarget);
            r.Apply(Main, Report("m", new NexusFleetGrid[0]), T0.AddSeconds(4));
            Assert.True(r.Exists(7));
        }

        /// <summary>Nexus closes the source before the target spawns, but the two reports race: the
        /// target's can land first.</summary>
        [Fact]
        public void TheReceivingServersReportFirst_StillMovesIt()
        {
            NexusFleetRegistry r = FlyingOnMain(T0);
            r.Apply(Day, Report("d", new[] { Row("k", 7) }), T0.AddSeconds(1));
            r.Apply(Main, Report("m", new NexusFleetGrid[0]), T0.AddSeconds(1));
            Assert.True(r.Exists(7));
            Assert.Equal(new long[] { 7 }, HeldBy(r, Day, T0.AddSeconds(1)));
        }

        [Fact]
        public void AGridNoServerReports_IsGoneAfterTheHold_AndKeepsItsDamage()
        {
            NexusFleetRegistry r = FlyingOnMain(T0);
            DateTime hit = T0.AddSeconds(1);
            r.Apply(Main, Report("m", new[] { Row("k", 7, hit) }), T0.AddSeconds(1));
            r.Apply(Main, Report("m", new NexusFleetGrid[0], new long[] { 7 }), T0.AddSeconds(2));

            r.Expire(T0.AddSeconds(2) + NexusFleetRegistry.TransitHold);
            Assert.True(r.Exists(7));
            r.Expire(T0.AddSeconds(3) + NexusFleetRegistry.TransitHold);
            Assert.False(r.Exists(7));
            Assert.False(r.ClosedByUs(7));
            Assert.Equal(hit, r.LastDamageUtc(7));
        }

        /// <summary>The new home's damage clock starts empty: the old home's hit still counts.</summary>
        [Fact]
        public void AMovedGrid_KeepsTheHitItTookBeforeTheBoundary()
        {
            NexusFleetRegistry r = FlyingOnMain(T0);
            DateTime hit = T0.AddSeconds(1);
            r.Apply(Main, Report("m", new[] { Row("k", 7, hit) }), T0.AddSeconds(1));
            r.Apply(Main, Report("m", new NexusFleetGrid[0], new long[] { 7 }), T0.AddSeconds(2));
            r.Apply(Day, Report("d", new[] { Row("k", 7) }), T0.AddSeconds(3));
            Assert.Equal(hit, r.LastDamageUtc(7));
        }

        [Fact]
        public void TheNewHomesCountersStartFromZero()
        {
            NexusFleetRegistry r = FlyingOnMain(T0);
            NexusFleetGrid before = Row("k", 7);
            before.ThrustLost = 3;
            r.Apply(Main, Report("m", new[] { before }), T0.AddSeconds(1));
            Assert.True(r.ConsumeThrustLost(7));
            NexusFleetGrid after = Row("k", 7);
            after.ThrustLost = 1;
            r.Apply(Day, Report("d", new[] { after }), T0.AddSeconds(2));
            Assert.True(r.ConsumeThrustLost(7));
        }

        [Fact]
        public void AWrittenOffGrid_OrOneUnderAnotherKey_IsNotTaken()
        {
            NexusFleetRegistry r = FlyingOnMain(T0);
            r.Apply(Day, Report("d", new[] { Row("other", 7) }), T0.AddSeconds(1));
            Assert.Empty(HeldBy(r, Day, T0.AddSeconds(1)));

            r.Apply(Main, Report("m", new NexusFleetGrid[0]), T0.AddSeconds(2));   // gone, not handed off
            r.Apply(Day, Report("d", new[] { Row("k", 7) }), T0.AddSeconds(3));
            Assert.False(r.Exists(7));
            Assert.Empty(HeldBy(r, Day, T0.AddSeconds(3)));
            Assert.Empty(r.TakeRehomed());
        }

        /// <summary>The source restarting while its grid is on the way does not write it off: it has
        /// left that server already.</summary>
        [Fact]
        public void TheSourceRestarting_LeavesAGridInTransitAlone()
        {
            NexusFleetRegistry r = FlyingOnMain(T0);
            r.Apply(Main, Report("m", new NexusFleetGrid[0], new long[] { 7 }), T0.AddSeconds(1));
            r.Apply(Main, Report("m2", null), T0.AddSeconds(2));
            Assert.True(r.Exists(7));
            r.Apply(Day, Report("d", new[] { Row("k", 7) }), T0.AddSeconds(3));
            Assert.Equal(new long[] { 7 }, HeldBy(r, Day, T0.AddSeconds(3)));
        }

        // ---- a grid the authority flew itself ------------------------------------------------

        [Fact]
        public void AnExpectedGrid_IsTakenByTheAgentThatReportsItsKey()
        {
            var r = new NexusFleetRegistry();
            r.Apply(Day, Report("d", null), T0);
            DateTime hit = T0.AddSeconds(-5);
            r.Expect(42, new NexusFleetOrder { Key = "local", Kind = (int)ActionKind.Trade, Tag = "ABC", Prefab = "Hauler" }, hit, T0);
            Assert.False(r.Owns(42));
            Assert.True(r.IsExpected(42));

            r.Apply(Day, Report("d", new[] { Row("wrong", 42) }), T0.AddSeconds(1));
            Assert.False(r.Owns(42));

            r.Apply(Day, Report("d", new[] { Row("local", 42) }), T0.AddSeconds(2));
            Assert.True(r.Exists(42));
            Assert.False(r.IsExpected(42));
            Assert.Equal(new long[] { 42 }, r.TakeRehomed());
            Assert.Equal(hit, r.LastDamageUtc(42));
            NexusFleetOrder adopt = OrdersFor(r, Day, T0.AddSeconds(2)).Single(o => o.Op == NexusFleetOp.Adopt);
            Assert.Equal("local", adopt.Key);
            Assert.Equal("ABC", adopt.Tag);
            Assert.Equal((int)ActionKind.Trade, adopt.Kind);
            Assert.Equal("Hauler", adopt.Prefab);
        }

        [Fact]
        public void AnExpectationNobodyAnswers_Lapses()
        {
            var r = new NexusFleetRegistry();
            r.Apply(Day, Report("d", null), T0);
            r.Expect(42, new NexusFleetOrder { Key = "local" }, null, T0);
            r.Expire(T0.AddSeconds(1) + NexusFleetRegistry.TransitHold);
            Assert.False(r.IsExpected(42));
            r.Apply(Day, Report("d", new[] { Row("local", 42) }), T0.AddSeconds(30));
            Assert.False(r.Owns(42));
        }

        [Fact]
        public void AGridArrivingOnTheAuthority_IsTakenBack_WithWhatItFlewAs()
        {
            NexusFleetRegistry r = FlyingOnMain(T0);
            DateTime hit = T0.AddSeconds(1);
            r.Apply(Main, Report("m", new[] { Row("k", 7, hit) }), T0.AddSeconds(1));
            r.Apply(Main, Report("m", new NexusFleetGrid[0], new long[] { 7 }), T0.AddSeconds(2));

            NexusFleetOrder template;
            DateTime? damaged;
            Assert.True(r.TakeBack(7, out template, out damaged));
            Assert.False(r.Owns(7));
            Assert.Equal("k", template.Key);
            Assert.Equal((int)ActionKind.Raid, template.Kind);
            Assert.Equal("DEF", template.Tag);
            Assert.Equal("mun-1", template.MunitionsId);
            Assert.Equal(hit, damaged);
            Assert.False(r.TakeBack(7, out template, out damaged));
        }

        [Fact]
        public void TheReportCarriesItsHandOffs_AndAnOldReportReadsNone()
        {
            NexusAgentReport back;
            Assert.True(NexusAgentReport.TryDecode(Report("s", null, new long[] { 5, 6 }).Encode(), out back));
            Assert.Equal(new long[] { 5, 6 }, back.HandedOff);
            Assert.True(NexusAgentReport.TryDecode(Report("s", null).Encode(), out back));
            Assert.Null(back.HandedOff);
        }
    }
}
