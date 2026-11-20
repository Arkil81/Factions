using System;

namespace Factions.Core
{
    /// <summary>
    /// Low-allocation timing aggregation for a bounded observation window. Every recorded call
    /// contributes to count, total, maximum, and strict budget-overrun evidence. A fixed histogram
    /// over a bounded recent-frame ring supplies conservative percentiles without per-frame or
    /// flush-time allocation/sorting.
    /// </summary>
    public sealed class FrameTimingWindow
    {
        // Sub-millisecond edges resolve an actor that is inside its budget; the 20/50/100 ms edges
        // resolve one that is far outside it. Without the upper edges every frame of an actor
        // averaging several ms lands in the last two buckets, so p95 and p99 both report 10.000 and
        // carry no information beyond "over 5 ms" -- while avg/max, which are exact, disagree with
        // them. Reported percentiles are bucket upper bounds, so more edges only ever tighten them.
        private static readonly double[] BucketUpperMilliseconds =
        {
            0.010, 0.025, 0.050, 0.100, 0.200, 0.300, 0.400, 0.500,
            0.750, 1.000, 2.000, 5.000, 10.000, 20.000, 50.000, 100.000,
        };

        private readonly long _frequency;
        private readonly long _budgetTicks;
        private readonly double _budgetMilliseconds;
        private readonly long[] _bucketUpperTicks;
        private readonly long[] _histogram;
        private byte[] _sampleBuckets;
        private int _nextSample;
        private int _sampleCount;

        public long CallCount { get; private set; }
        public long TotalTicks { get; private set; }
        public long MaxTicks { get; private set; }
        public long OverBudgetCount { get; private set; }
        /// <summary>Total ticks by which recorded calls exceeded the budget, summed over the window.
        /// Ranks actors by how much attributable over-budget time they actually produce, which
        /// neither an average (blind to call count) nor a maximum (blind to frequency) does.</summary>
        public long TotalExcessTicks { get; private set; }
        public long SampleCount { get { return _sampleCount; } }
        public double BudgetMilliseconds { get { return _budgetMilliseconds; } }

        public FrameTimingWindow(long frequency, double budgetMilliseconds, int sampleCapacity)
        {
            if (frequency <= 0) throw new ArgumentOutOfRangeException("frequency");
            if (budgetMilliseconds < 0) throw new ArgumentOutOfRangeException("budgetMilliseconds");
            if (sampleCapacity <= 0) throw new ArgumentOutOfRangeException("sampleCapacity");

            _frequency = frequency;
            _budgetMilliseconds = budgetMilliseconds;
            _budgetTicks = (long)Math.Ceiling(budgetMilliseconds * frequency / 1000.0);
            _bucketUpperTicks = new long[BucketUpperMilliseconds.Length];
            for (int i = 0; i < BucketUpperMilliseconds.Length; i++)
                _bucketUpperTicks[i] = (long)Math.Ceiling(BucketUpperMilliseconds[i] * frequency / 1000.0);
            // The final histogram slot is the open-ended overflow bucket.
            _histogram = new long[_bucketUpperTicks.Length + 1];
            _sampleBuckets = new byte[sampleCapacity];
        }

        /// <summary>Grows the recent-call sample ring so percentiles can cover a longer window.
        /// <para>Percentiles read only the most recent <see cref="SampleCount"/> calls, while count,
        /// average, maximum and over-budget evidence cover the whole window. Once a window records
        /// more calls than the ring holds, those two halves of one row describe different spans with
        /// nothing on the row saying so, so the ring has to be sized from the window it serves.</para>
        /// <para>Growing discards the samples already held; the exact statistics are untouched.
        /// Shrinking is not supported and a smaller capacity is ignored.</para></summary>
        public void EnsureSampleCapacity(int capacity)
        {
            if (capacity <= _sampleBuckets.Length) return;
            _sampleBuckets = new byte[capacity];
            Array.Clear(_histogram, 0, _histogram.Length);
            _nextSample = 0;
            _sampleCount = 0;
        }

        public void Record(long elapsedTicks)
        {
            if (elapsedTicks < 0) elapsedTicks = 0;
            CallCount++;
            TotalTicks += elapsedTicks;
            if (elapsedTicks > MaxTicks) MaxTicks = elapsedTicks;
            // The contract is strictly below the budget, so equality is an overrun.
            if (elapsedTicks >= _budgetTicks)
            {
                OverBudgetCount++;
                TotalExcessTicks += elapsedTicks - _budgetTicks;
            }

            int bucket = GetBucket(elapsedTicks);
            if (_sampleCount == _sampleBuckets.Length)
                _histogram[_sampleBuckets[_nextSample]]--;
            else
                _sampleCount++;
            _sampleBuckets[_nextSample] = (byte)bucket;
            _histogram[bucket]++;
            _nextSample++;
            if (_nextSample >= _sampleBuckets.Length) _nextSample = 0;
        }

        public double AverageMilliseconds()
        {
            return CallCount == 0 ? 0.0 : ToMilliseconds(TotalTicks) / CallCount;
        }

        /// <summary>Every recorded call summed. This is the actor's share of the window, which an
        /// average cannot express on its own: an actor averaging 0.1 ms over 3600 calls costs 36x one
        /// averaging 10 ms over a single call.</summary>
        public double TotalMilliseconds()
        {
            return ToMilliseconds(TotalTicks);
        }

        public double ExcessMilliseconds()
        {
            return ToMilliseconds(TotalExcessTicks);
        }

        public double MaxMilliseconds()
        {
            return ToMilliseconds(MaxTicks);
        }

        public double PercentileMilliseconds(double percentile)
        {
            if (_sampleCount == 0) return 0.0;
            if (percentile < 0.0) percentile = 0.0;
            if (percentile > 1.0) percentile = 1.0;

            long target = (long)Math.Ceiling(percentile * _sampleCount);
            if (target < 1) target = 1;
            long seen = 0;
            for (int i = 0; i < _histogram.Length; i++)
            {
                seen += _histogram[i];
                if (seen < target) continue;
                // Bucket bounds are reported rather than a lower estimate. The overflow bucket
                // reports the exact maximum, which is also conservative for the selected sample.
                return i < _bucketUpperTicks.Length
                    ? ToMilliseconds(_bucketUpperTicks[i])
                    : MaxMilliseconds();
            }
            return MaxMilliseconds();
        }

        public void Reset()
        {
            CallCount = 0;
            TotalTicks = 0;
            MaxTicks = 0;
            OverBudgetCount = 0;
            TotalExcessTicks = 0;
            Array.Clear(_histogram, 0, _histogram.Length);
            _nextSample = 0;
            _sampleCount = 0;
        }

        private int GetBucket(long elapsedTicks)
        {
            for (int i = 0; i < _bucketUpperTicks.Length; i++)
                if (elapsedTicks <= _bucketUpperTicks[i]) return i;
            return _bucketUpperTicks.Length;
        }

        private double ToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / _frequency;
        }
    }
}
