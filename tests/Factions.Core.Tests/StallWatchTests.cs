using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    /// <summary>One tick is one millisecond here.</summary>
    public class StallWatchTests
    {
        private static StallWatch Armed(long now)
        {
            var w = new StallWatch(2000, 10000, 1000);
            w.Arm();
            w.Enter("base", now);
            return w;
        }

        [Fact]
        public void Nothing_is_reported_while_disarmed()
        {
            var w = new StallWatch(2000, 10000, 1000);
            w.Enter("agent.relay", 1);
            Assert.Equal(StallEventKind.None, w.Poll(5000, 0).Kind);
            Assert.Equal(StallEventKind.None, w.Poll(30000, 0).Kind);
        }

        [Fact]
        public void A_boundary_from_before_the_arm_is_not_a_stall()
        {
            var w = new StallWatch(2000, 10000, 1000);
            w.Enter("outside", 1);
            w.Arm();
            Assert.Equal(StallEventKind.None, w.Poll(60000, 0).Kind);
            w.Enter("base", 60001);
            Assert.Equal(StallEventKind.None, w.Poll(60250, 0).Kind);
        }

        [Fact]
        public void Frames_that_keep_moving_never_stall()
        {
            var w = Armed(1);
            for (long t = 2; t < 20000; t += 17)
            {
                w.Enter("agent.relay", t);
                w.Enter("outside", t + 5);
                if (t % 250 < 17) Assert.Equal(StallEventKind.None, w.Poll(t + 6, 0).Kind);
            }
        }

        [Fact]
        public void A_stuck_section_is_named_while_stuck_repeated_and_ended_exactly()
        {
            var w = Armed(1000);
            w.Enter("agent.gridAssembly", 1100);
            Assert.Equal(StallEventKind.None, w.Poll(2000, 0).Kind);

            StallEvent stall = w.Poll(3200, 0);
            Assert.Equal(StallEventKind.Stall, stall.Kind);
            Assert.Equal("agent.gridAssembly", stall.Section);
            Assert.Equal(2100, stall.Ms, 0);

            Assert.Equal(StallEventKind.None, w.Poll(3450, 0).Kind);
            StallEvent ongoing = w.Poll(13300, 0);
            Assert.Equal(StallEventKind.Ongoing, ongoing.Kind);
            Assert.Equal(12200, ongoing.Ms, 0);

            w.Enter("agent.rockContact", 27849);
            w.Enter("outside", 27850);
            StallEvent end = w.Poll(28000, 0);
            Assert.Equal(StallEventKind.End, end.Kind);
            Assert.Equal("agent.gridAssembly", end.Section);
            Assert.True(end.ExactEnd);
            Assert.Equal(26749, end.Ms, 0);

            Assert.Equal(StallEventKind.None, w.Poll(28250, 0).Kind);
        }

        [Fact]
        public void A_suspended_process_shows_as_a_poll_gap_as_long_as_the_stall()
        {
            var w = Armed(1000);
            Assert.Equal(StallEventKind.None, w.Poll(1250, 7).Kind);
            w.Enter("agent.relay", 1300);
            // Watchdog frozen with the game thread: its next poll lands 26 s later, after a collection.
            StallEvent stall = w.Poll(27300, 8);
            Assert.Equal(StallEventKind.Stall, stall.Kind);
            Assert.Equal(26050, stall.PollGapMs, 0);
            Assert.Equal(1, stall.Gc2);

            w.Enter("outside", 27400);
            StallEvent end = w.Poll(27550, 8);
            Assert.Equal(StallEventKind.End, end.Kind);
            Assert.Equal(1, end.Gc2);
            Assert.Equal(26100, end.Ms, 0);
        }

        [Fact]
        public void Disarming_mid_stall_drops_it_without_an_end()
        {
            var w = Armed(1000);
            w.Enter("bridge", 1000);
            Assert.Equal(StallEventKind.Stall, w.Poll(3500, 0).Kind);
            w.Disarm();
            Assert.Equal(StallEventKind.None, w.Poll(3750, 0).Kind);
            w.Arm();
            w.Enter("base", 4000);
            Assert.Equal(StallEventKind.None, w.Poll(4250, 0).Kind);
        }

        [Fact]
        public void Lines_carry_the_discriminating_values()
        {
            Assert.Equal("FACTIONS_STALL section=agent.relay elapsedMs=2100 pollGapMs=250 gc2=+0",
                new StallEvent(StallEventKind.Stall, "agent.relay", 2100.4, 250, 0, false).Format());
            Assert.Equal("FACTIONS_STALL_ONGOING section=outside elapsedMs=12100 pollGapMs=250 gc2=+1",
                new StallEvent(StallEventKind.Ongoing, "outside", 12100, 250, 1, false).Format());
            Assert.Equal("FACTIONS_STALL_END section=agent.relay ms=26749 end=exact pollGapMs=250 gc2=+0",
                new StallEvent(StallEventKind.End, "agent.relay", 26749, 250, 0, true).Format());
            Assert.Equal("", StallEvent.None.Format());
        }
    }
}
