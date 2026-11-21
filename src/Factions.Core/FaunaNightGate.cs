using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Holds a <see cref="FaunaRule.OncePerNight"/> rule to one wave per area per night.
    ///
    /// <para><b>Why SE needs this.</b> <c>MySpaceFaunaComponent</c> times a wave from a 150 m spawn point
    /// around a player who stays put, and clears the point within 40 s of the wave. The next update
    /// opens a fresh point at the same spot, so a player at a base is sent another wave every
    /// <see cref="FaunaRule.DelaySeconds"/> plus up to 40 s, all night. A kill only quiets the 150 m
    /// around the body.</para>
    ///
    /// <para><b>The rule.</b> The first wave of a rule at a spot is admitted and opens a lock of
    /// <see cref="RadiusM"/> around it. Until the lock expires, every later wave of the same rule whose
    /// spawn point lies inside it is refused. A wave is one spawn point's batch of bots, so each bot
    /// SE asks for carries the point as its <c>wave</c> token, and every bot of an admitted wave is
    /// admitted.</para>
    ///
    /// <para><b>How long a lock lasts.</b> <see cref="LockMs"/>: half the world's day, which is the
    /// night at the equator. Shorter than the day that follows, so the next night is not refused;
    /// as long as the night, so the same night cannot open a second wave. The lock is in memory only:
    /// a restart clears it.</para>
    /// </summary>
    public sealed class FaunaNightGate
    {
        /// <summary>Metres around an admitted wave's spawn point that the rule stays quiet. A base and
        /// its surroundings; a player who travels farther meets a new pack.</summary>
        public const double RadiusM = 3000.0;

        /// <summary>The lock used when the sun does not move: an hour.</summary>
        public const long FixedSunLockMs = 3600L * 1000L;

        private sealed class Lock
        {
            public object Rule;
            public double X, Y, Z;
            public long UntilMs;
            public object Wave;
        }

        private readonly List<Lock> _locks = new List<Lock>();

        /// <summary>So a refused wave counts once, not once per bot.</summary>
        private object _lastRefused;

        /// <summary>Locks currently held, for the debug readback.</summary>
        public int Held { get { return _locks.Count; } }

        /// <summary>Waves refused, this process.</summary>
        public int Refused { get; private set; }

        /// <summary>Waves admitted that opened a lock, this process.</summary>
        public int Opened { get; private set; }

        /// <summary>Half of a world day of <paramref name="sunRotationMinutes"/>, in milliseconds; or
        /// <see cref="FixedSunLockMs"/> when the sun does not rotate.</summary>
        public static long LockMs(bool sunRotates, double sunRotationMinutes)
        {
            if (!sunRotates || !(sunRotationMinutes > 0) || double.IsInfinity(sunRotationMinutes)) return FixedSunLockMs;
            return (long)Math.Round(sunRotationMinutes * 60.0 * 1000.0 / 2.0);
        }

        /// <summary>Whether one bot of <paramref name="wave"/> may spawn. <paramref name="rule"/> is
        /// compared by reference; (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) is
        /// the wave's spawn point in world metres.</summary>
        public bool Admit(object rule, object wave, double x, double y, double z, long nowMs, long lockMs)
        {
            if (rule == null || wave == null) return true;
            double r2 = RadiusM * RadiusM;
            for (int i = _locks.Count - 1; i >= 0; i--)
            {
                Lock l = _locks[i];
                if (l.UntilMs - nowMs <= 0) { _locks.RemoveAt(i); continue; }
                if (!ReferenceEquals(l.Rule, rule)) continue;
                double dx = l.X - x, dy = l.Y - y, dz = l.Z - z;
                if (dx * dx + dy * dy + dz * dz > r2) continue;
                if (ReferenceEquals(l.Wave, wave)) return true;
                if (!ReferenceEquals(_lastRefused, wave)) { Refused++; _lastRefused = wave; }
                return false;
            }
            _locks.Add(new Lock { Rule = rule, Wave = wave, X = x, Y = y, Z = z, UntilMs = nowMs + lockMs });
            Opened++;
            return true;
        }

        /// <summary>Drops every lock: a reload of the policy.</summary>
        public void Clear()
        {
            _locks.Clear();
            _lastRefused = null;
        }
    }
}
