namespace Factions.Core
{
    /// <summary>What a hovering grid's dampener balance implies, from SE's own hold rule. SE's dampener asks for
    /// <c>m_d * (g - 2 v)</c> upward, where <c>m_d</c> is the mass it believes and <c>v</c> the upward speed, so a
    /// grid settles where that equals its true weight. No SE dependencies; used by <c>factions dampprobe</c>.</summary>
    public static class DampenerBalance
    {
        /// <summary>The upward speed, m/s, at which the hold settles when the dampener believes
        /// <paramref name="dampenerKg"/> and the body has <paramref name="bodyKg"/>: <c>g (1 - m/m_d) / 2</c>.
        /// Positive is a climb. Zero when either mass is not positive.</summary>
        public static double EquilibriumClimbMs(double gravityMs2, double bodyKg, double dampenerKg)
        {
            if (bodyKg <= 0 || dampenerKg <= 0) return 0;
            return gravityMs2 * (1.0 - bodyKg / dampenerKg) / 2.0;
        }

        /// <summary>The upward force, N, that thrust is NOT supplying against the body's weight: weight minus
        /// thrust lift. At a steady climb with no other force this is zero whatever the dampener believes; a
        /// positive value is an upward push from elsewhere, or gravity the body did not receive.</summary>
        public static double UnaccountedLiftN(double gravityMs2, double bodyKg, double thrustLiftN)
        {
            return gravityMs2 * bodyKg - thrustLiftN;
        }
    }
}
