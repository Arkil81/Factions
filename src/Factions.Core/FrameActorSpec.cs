namespace Factions.Core
{
    /// <summary>Registration data for one actor the frame scheduler admits work for. Covers both the
    /// runtime drains and the host-side observers: the difference between them is who dispatches the
    /// body, which is not something the scheduler needs to know.</summary>
    public sealed class FrameActorSpec
    {
        /// <summary>Matches the actor's perf.json key, so telemetry lines up with the buckets.</summary>
        public string Name;

        /// <summary>Wall-clock interval between runs. Zero means every frame.</summary>
        public double IntervalSeconds;

        /// <summary>Cold-start cost estimate, used only to decide whether this actor can be admitted
        /// as a second-or-later unit in a frame. The first admission of a frame ignores it, so a
        /// wrong seed can delay an actor by at most one frame, never starve it.</summary>
        public double SeedCostMilliseconds;

        /// <summary>How overdue this actor may get before it is admitted regardless of remaining
        /// budget. Zero derives a default from the interval.</summary>
        public double MaxDebtSeconds;

        /// <summary>True when the actor can return <see cref="FrameActorOutcome.Yielded"/>, i.e. it
        /// owns a cursor and expects to be resumed.</summary>
        public bool Resumable;

        /// <summary>Offset applied to this actor's first due time, so actors sharing an interval do
        /// not all land on the same frame forever.</summary>
        public double InitialPhaseOffsetSeconds;
    }
}
