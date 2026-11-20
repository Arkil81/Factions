namespace Factions.Core
{
    /// <summary>What an admitted actor did with its unit. Replaces the three mutually inconsistent
    /// meanings the drains' bool returns used to carry.</summary>
    public enum FrameActorOutcome
    {
        /// <summary>A precondition failed and no real work ran (no world, spawner skipping transit).
        /// Does not update the cost estimate - a near-zero sample would make the actor look free.</summary>
        Skipped,

        /// <summary>Admitted, but found nothing due to do. Real time was spent, so it does update the
        /// estimate.</summary>
        NotDue,

        /// <summary>Did real work; its sweep is incomplete and expects to be resumed. The actor is
        /// deliberately not rescheduled, so its overdue ratio keeps rising and it keeps winning
        /// selection until it completes.</summary>
        Yielded,

        /// <summary>Finished its whole unit or sweep. Rescheduled from when it became due, not from
        /// now, so a late completion does not push the following run out too.</summary>
        Completed,
    }
}
