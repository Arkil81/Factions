using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class TimedHoldTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 9, 16, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Held_until_the_deadline_and_not_at_it()
        {
            var hold = new TimedHold<long>();
            hold.Arm(7, T0, TimeSpan.FromSeconds(10));

            Assert.True(hold.IsHeld(7, T0));
            Assert.True(hold.IsHeld(7, T0.AddSeconds(9.99)));
            Assert.False(hold.IsHeld(7, T0.AddSeconds(10)));
        }

        [Fact]
        public void An_expired_query_drops_the_entry()
        {
            var hold = new TimedHold<long>();
            hold.Arm(7, T0, TimeSpan.FromSeconds(10));

            Assert.False(hold.IsHeld(7, T0.AddSeconds(11)));
            Assert.Equal(0, hold.Count);
            DateTime since;
            Assert.False(hold.TryGetSince(7, out since));
        }

        [Fact]
        public void An_unknown_key_is_not_held()
        {
            var hold = new TimedHold<string>(StringComparer.Ordinal);
            Assert.False(hold.IsHeld("A1", T0));
            Assert.Equal(0, hold.Count);
        }

        [Fact]
        public void Prune_drops_only_what_has_expired()
        {
            var hold = new TimedHold<long>();
            hold.Arm(1, T0, TimeSpan.FromSeconds(5));
            hold.Arm(2, T0, TimeSpan.FromSeconds(60));
            hold.Arm(3, T0.AddSeconds(4), TimeSpan.FromSeconds(5));

            hold.Prune(T0.AddSeconds(6));

            Assert.Equal(2, hold.Count);
            Assert.False(hold.IsHeld(1, T0.AddSeconds(6)));
            Assert.True(hold.IsHeld(2, T0.AddSeconds(6)));
            Assert.True(hold.IsHeld(3, T0.AddSeconds(6)));
        }

        [Fact]
        public void Rearming_moves_the_deadline_and_the_since()
        {
            var hold = new TimedHold<long>();
            hold.Arm(7, T0, TimeSpan.FromSeconds(5));
            hold.Arm(7, T0.AddSeconds(4), TimeSpan.FromSeconds(5));

            Assert.True(hold.IsHeld(7, T0.AddSeconds(8)));
            DateTime since;
            Assert.True(hold.TryGetSince(7, out since));
            Assert.Equal(T0.AddSeconds(4), since);
            Assert.Equal(1, hold.Count);
        }

        [Fact]
        public void Release_and_clear_forget_the_key()
        {
            var hold = new TimedHold<long>();
            hold.Arm(1, T0, TimeSpan.FromMinutes(1));
            hold.Arm(2, T0, TimeSpan.FromMinutes(1));

            hold.Release(1);
            Assert.False(hold.IsHeld(1, T0));
            Assert.True(hold.IsHeld(2, T0));

            hold.Clear();
            Assert.Equal(0, hold.Count);
            Assert.False(hold.IsHeld(2, T0));
        }
    }
}
