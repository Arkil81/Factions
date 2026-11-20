using System;
using Factions.Core;
using Xunit;

namespace Factions.Core.Tests
{
    public class SustainedConditionTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Confirms_only_once_the_condition_has_held_for_the_duration()
        {
            var cond = new SustainedCondition<long>(TimeSpan.FromSeconds(5));

            Assert.False(cond.Observe(7, true, T0));
            Assert.False(cond.Observe(7, true, T0.AddSeconds(4.99)));
            Assert.True(cond.Observe(7, true, T0.AddSeconds(5)));
        }

        [Fact]
        public void A_false_observation_restarts_the_run()
        {
            var cond = new SustainedCondition<long>(TimeSpan.FromSeconds(5));

            Assert.False(cond.Observe(7, true, T0));
            Assert.False(cond.Observe(7, false, T0.AddSeconds(4)));
            Assert.Equal(0, cond.Count);
            Assert.False(cond.Observe(7, true, T0.AddSeconds(6)));
            Assert.False(cond.Observe(7, true, T0.AddSeconds(10)));
            Assert.True(cond.Observe(7, true, T0.AddSeconds(11)));
        }

        [Fact]
        public void Confirmation_fires_once_and_drops_the_key()
        {
            var cond = new SustainedCondition<long>(TimeSpan.FromSeconds(5));

            cond.Observe(7, true, T0);
            Assert.True(cond.Observe(7, true, T0.AddSeconds(5)));
            Assert.Equal(0, cond.Count);
            Assert.False(cond.Observe(7, true, T0.AddSeconds(6)));
        }

        [Fact]
        public void Keys_are_timed_independently()
        {
            var cond = new SustainedCondition<long>(TimeSpan.FromSeconds(5));

            cond.Observe(1, true, T0);
            cond.Observe(2, true, T0.AddSeconds(3));
            Assert.True(cond.Observe(1, true, T0.AddSeconds(5)));
            Assert.False(cond.Observe(2, true, T0.AddSeconds(5)));
            Assert.Equal(1, cond.Count);
        }

        [Fact]
        public void Forget_drops_a_timed_key()
        {
            var cond = new SustainedCondition<long>(TimeSpan.FromSeconds(5));

            cond.Observe(7, true, T0);
            cond.Forget(7);
            Assert.Equal(0, cond.Count);
            Assert.False(cond.Observe(7, true, T0.AddSeconds(5)));
        }

        [Fact]
        public void A_never_true_key_is_never_tracked()
        {
            var cond = new SustainedCondition<long>(TimeSpan.FromSeconds(5));

            Assert.False(cond.Observe(7, false, T0));
            Assert.Equal(0, cond.Count);
        }
    }
}
