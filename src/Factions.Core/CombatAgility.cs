using System;

namespace Factions.Core
{
    /// <summary>What a fighting hull's agility buys it this second: the least speed it keeps while
    /// it holds a range, and how hard it manoeuvres, 0..1.</summary>
    public struct CombatMotion
    {
        /// <summary>0 for a gun platform, 1 for a hull at <see cref="CombatAgility.FullAccelMs2"/> or more.</summary>
        public double Agility;

        /// <summary>The least speed, relative to its target, the hull keeps while holding a range;
        /// 0 lets it come to rest. See <see cref="FlightSample.MinCombatSpeedMs"/>.</summary>
        public double FloorMs;
    }

    /// <summary>
    /// A fighting hull's agility — its thrust over its mass, the figure the contested duel calls
    /// combat mobility (<see cref="CombatMobility"/>) — turned into how it moves in a fight: a fast
    /// hull never drops below a floor speed while it holds a range, because speed is its defence; a
    /// heavy hull's floor is zero, so it may still hold still as a gun platform. See
    /// docs/fleet-tactics.md, "Moving while holding a range".
    ///
    /// <para>Code constants, not tuning: they shape a manoeuvre, they are not a balance knob.</para>
    /// </summary>
    public static class CombatAgility
    {
        /// <summary>At or below this acceleration a hull has no floor: a gun platform.</summary>
        public const double RestAccelMs2 = 2.0;

        /// <summary>At or above this acceleration a hull has the full <see cref="MaxFloorMs"/>.</summary>
        public const double FullAccelMs2 = 15.0;

        /// <summary>The floor of a fully agile hull, m/s, before the orbit's own centripetal cap.</summary>
        public const double MaxFloorMs = 40.0;

        /// <summary>A hull's combat acceleration, m/s²: its strongest single thrust direction over its
        /// mass, the same axis rule <see cref="CombatMobility.OfHull"/> reads from a prefab. 0 with no
        /// thrust or no mass.</summary>
        public static double AccelMs2(AxisThrust thrust, double massKg)
        {
            if (massKg <= 0) return 0;
            double best = thrust.UpN;
            if (thrust.DownN > best) best = thrust.DownN;
            if (thrust.LeftN > best) best = thrust.LeftN;
            if (thrust.RightN > best) best = thrust.RightN;
            if (thrust.ForwardN > best) best = thrust.ForwardN;
            if (thrust.BackwardN > best) best = thrust.BackwardN;
            return best > 0 ? best / massKg : 0;
        }

        /// <summary>Agility 0..1 from a combat acceleration: 0 at or below <see cref="RestAccelMs2"/>,
        /// 1 at or above <see cref="FullAccelMs2"/>, linear between.</summary>
        public static double Agility(double accelMs2)
        {
            if (double.IsNaN(accelMs2) || accelMs2 <= RestAccelMs2) return 0;
            if (accelMs2 >= FullAccelMs2) return 1;
            return (accelMs2 - RestAccelMs2) / (FullAccelMs2 - RestAccelMs2);
        }

        /// <summary>How a hull of <paramref name="accelMs2"/> moves in a fight. A hull held in
        /// formation — a commander's hold or form-up order — gets nothing: formation wins, and it
        /// holds where it is put.</summary>
        public static CombatMotion Motion(double accelMs2, bool formationHeld)
        {
            if (formationHeld) return new CombatMotion();
            double agility = Agility(accelMs2);
            return new CombatMotion { Agility = agility, FloorMs = MaxFloorMs * agility };
        }

        /// <summary>Which way a hull circles its target, +1 or -1, fixed per grid.</summary>
        public static int OrbitSense(long seed)
        {
            return (Evasion.Mix(seed, 0x0bb17) & 1UL) == 0 ? 1 : -1;
        }
    }

    /// <summary>The direction of an evasive burst, in the hull's line-of-sight frame: across the line
    /// to its target, along the orbit axis, or a boost along its own orbit.</summary>
    public enum EvadeDir
    {
        None = 0,
        Left = 1,
        Right = 2,
        Up = 3,
        Down = 4,
        Boost = 5,
    }

    /// <summary>One hull's evasive bursts: the one running, and when the next may start. Held by the
    /// host per fighting grid; <see cref="Evasion.Consider"/> is the only writer.</summary>
    public struct EvadeState
    {
        public EvadeDir Dir;
        public DateTime StartUtc;
        public DateTime UntilUtc;
        public double DurationSec;
        public double SpeedMs;

        /// <summary>No burst starts before this; set at each start. Longer than a hit stays fresh,
        /// so one hit never starts two bursts.</summary>
        public DateTime NextAllowedUtc;

        /// <summary>Bursts started so far; the second half of the seed.</summary>
        public int Count;
    }

    /// <summary>
    /// A hull that is hit breaks hard — across the line to its target, up or down its orbit axis, or
    /// a boost along its orbit — for a short, seeded time, then resumes its orbit. Only under fire:
    /// there is no continuous jinking. Rate-limited so continuous fire cannot keep a hull in bursts,
    /// and deterministic for a grid's seed and burst count. A hull held in formation never breaks.
    ///
    /// <para>Code constants, not tuning.</para></summary>
    public static class Evasion
    {
        public const double MinBurstSec = 1.5;
        public const double MaxBurstSec = 3.0;

        /// <summary>Least time between the starts of two bursts.</summary>
        public const double MinIntervalSec = 8.0;

        /// <summary>A hit older than this starts nothing: the burst answers the hit, not its memory.</summary>
        public const double HitFreshSec = 1.0;

        /// <summary>A fully agile hull's burst runs this much faster than its floor, m/s.</summary>
        public const double BurstExtraMs = 25.0;

        /// <summary>True while a burst runs at <paramref name="nowUtc"/>.</summary>
        public static bool Active(EvadeState st, DateTime nowUtc)
        {
            return st.Dir != EvadeDir.None && nowUtc < st.UntilUtc;
        }

        /// <summary>
        /// Starts a burst when the hull was hit within <see cref="HitFreshSec"/>, no burst is running, <see cref="MinIntervalSec"/> has passed since the last
        /// start, the hull has any agility and is not held in formation. Returns true on the call that
        /// starts one. An ended burst is cleared; a running one is cut when formation takes the hull.
        ///
        /// <para>The direction and duration come from <paramref name="seed"/> and the burst count
        /// alone. In gravity a downward break becomes an upward one.</para></summary>
        public static bool Consider(ref EvadeState st, DateTime nowUtc, DateTime? lastHitUtc, long seed,
                                    CombatMotion motion, bool formationHeld, bool inGravity)
        {
            if (formationHeld)
            {
                st.Dir = EvadeDir.None;
                return false;
            }
            if (st.Dir != EvadeDir.None && nowUtc >= st.UntilUtc) st.Dir = EvadeDir.None;
            if (st.Dir != EvadeDir.None) return false;

            if (!lastHitUtc.HasValue || motion.Agility <= 0) return false;
            DateTime hit = lastHitUtc.Value;
            if ((nowUtc - hit).TotalSeconds > HitFreshSec) return false;
            if (nowUtc < st.NextAllowedUtc) return false;

            ulong h = Mix(seed, st.Count);
            var dir = (EvadeDir)(1 + (int)(h % 5UL));
            if (inGravity && dir == EvadeDir.Down) dir = EvadeDir.Up;
            double u = ((h >> 8) & 0xFFFFUL) / 65535.0;

            st.Dir = dir;
            st.Count++;
            st.StartUtc = nowUtc;
            st.DurationSec = MinBurstSec + (MaxBurstSec - MinBurstSec) * u;
            st.UntilUtc = nowUtc + TimeSpan.FromSeconds(st.DurationSec);
            st.NextAllowedUtc = nowUtc + TimeSpan.FromSeconds(MinIntervalSec);
            st.SpeedMs = motion.FloorMs + BurstExtraMs * motion.Agility;
            return true;
        }

        /// <summary>SplitMix64's finalizer over a seed and a salt: portable and allocation-free.</summary>
        internal static ulong Mix(long seed, long salt)
        {
            ulong x = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + (ulong)salt + 0x9E3779B97F4A7C15UL);
            x = unchecked((x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL);
            x = unchecked((x ^ (x >> 27)) * 0x94D049BB133111EBUL);
            return x ^ (x >> 31);
        }
    }
}
