namespace Factions
{
    /// <summary>The current <see cref="Factions.Core.StallBackoff.Scale"/>, set by the governor each window
    /// and read by every per-frame budget it scales. 1 when the switch is off. Game thread.</summary>
    internal static class StallBackoffScale
    {
        internal static double Value = 1.0;
    }
}
