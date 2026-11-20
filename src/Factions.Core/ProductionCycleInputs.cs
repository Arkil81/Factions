using System;

namespace Factions.Core
{
    /// <summary>
    /// Immutable begin-time inputs for a future staged Production transaction. This aggregates its
    /// world/faction read-set guard, frozen semantic time, and deep-copied production tuning without
    /// exposing any live mutable input to the transaction after capture.
    /// </summary>
    public sealed class ProductionCycleInputs
    {
        private readonly ProductionCycleCommitGuard _guard;

        private ProductionCycleInputs(ProductionCycleCommitGuard guard, ProductionTuningSnapshot tuning)
        {
            _guard = guard;
            Tuning = tuning;
            CapturedUtc = guard.CapturedUtc;
        }

        public DateTime CapturedUtc { get; private set; }
        public ProductionTuningSnapshot Tuning { get; private set; }

        public static ProductionCycleInputs Capture(FactionState state, WorldModel world, FactionTuning tuning)
        {
            if (tuning == null) throw new ArgumentNullException(nameof(tuning));
            ProductionCycleCommitGuard guard = ProductionCycleCommitGuard.Capture(state, world);
            return new ProductionCycleInputs(guard, ProductionTuningSnapshot.Capture(tuning));
        }

        /// <summary>
        /// Returns false only when the captured mutable faction/world read set changed. Advancing
        /// wall-clock time alone is valid because the transaction uses <see cref="CapturedUtc"/>.
        /// </summary>
        public bool IsStillValid(FactionState state, WorldModel world)
        {
            return _guard.IsStillValid(state, world);
        }
    }
}
