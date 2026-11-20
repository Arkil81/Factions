using System.Diagnostics;

namespace Factions.Core
{
    /// <summary>
    /// Soft deadline for bounded runtime work, shared by the host's per-Update() frame scheduler and
    /// any Core manager capable of resumable work (see <see cref="ProductionManager"/>'s staged cycle).
    /// Lives in Core (not the Torch host project) so a manager can check it without a host dependency.
    /// </summary>
    public struct FrameDeadline
    {
        // A day. Large enough that a synchronous whole-roster pass can never reach it, small enough
        // to stay far inside long-tick arithmetic.
        private const double UnboundedMilliseconds = 86_400_000.0;

        private readonly long _startTimestamp;
        private readonly long _softBudgetTicks;
        private readonly double _softBudgetMilliseconds;

        public FrameDeadline(long startTimestamp, double softBudgetMilliseconds)
        {
            _startTimestamp = startTimestamp;
            _softBudgetMilliseconds = softBudgetMilliseconds;
            _softBudgetTicks = (long)(softBudgetMilliseconds * Stopwatch.Frequency / 1000.0);
        }

        /// <summary>A deadline that never expires, for the synchronous <c>Tick()</c> wrappers and the
        /// offline sim, which run a whole roster pass in one call by design.</summary>
        public static FrameDeadline Unbounded()
        {
            return new FrameDeadline(Stopwatch.GetTimestamp(), UnboundedMilliseconds);
        }

        public double BudgetMilliseconds { get { return _softBudgetMilliseconds; } }

        /// <summary>The timestamp this deadline was started from, so a frame-wide budget can be
        /// accounted against the same origin the host used to build it.</summary>
        public long StartTimestamp { get { return _startTimestamp; } }

        public bool IsExpired()
        {
            return Stopwatch.GetTimestamp() - _startTimestamp >= _softBudgetTicks;
        }

        public double ElapsedMilliseconds()
        {
            return (Stopwatch.GetTimestamp() - _startTimestamp) * 1000.0 / Stopwatch.Frequency;
        }

        /// <summary>Budget left before this deadline expires. Goes negative once it has, which is how
        /// far the admitted unit overran by.</summary>
        public double RemainingMilliseconds()
        {
            return _softBudgetMilliseconds - ElapsedMilliseconds();
        }
    }
}
