using System;

namespace Factions.Core
{
    /// <summary>
    /// Converts active elapsed time into fixed-duration work quanta. When consumption is capped,
    /// unconsumed quanta remain buffered for a later call rather than being discarded.
    /// </summary>
    public sealed class FixedIntervalAccumulator
    {
        private double _accumulatedSeconds;

        public double AccumulatedSeconds { get { return _accumulatedSeconds; } }

        public double SecondsUntilNext(double intervalSeconds)
        {
            if (double.IsNaN(intervalSeconds) || double.IsInfinity(intervalSeconds) || intervalSeconds <= 0)
                return double.PositiveInfinity;

            double remaining = intervalSeconds - _accumulatedSeconds;
            return remaining > 0 ? remaining : 0;
        }

        public int Consume(double elapsedSeconds, double intervalSeconds, int maxQuanta)
        {
            if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds <= 0)
                return 0;
            if (double.IsNaN(intervalSeconds) || double.IsInfinity(intervalSeconds) || intervalSeconds <= 0)
                return 0;
            if (maxQuanta <= 0) return 0;

            _accumulatedSeconds += elapsedSeconds;
            double availableValue = Math.Floor(_accumulatedSeconds / intervalSeconds);
            int available = availableValue > int.MaxValue ? int.MaxValue : (int)availableValue;
            if (available <= 0) return 0;

            int consumed = available < maxQuanta ? available : maxQuanta;
            _accumulatedSeconds -= consumed * intervalSeconds;
            return consumed;
        }

        public void Reset()
        {
            _accumulatedSeconds = 0;
        }
    }
}
