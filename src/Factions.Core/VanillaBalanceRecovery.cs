namespace Factions.Core
{
    /// <summary>
    /// Whether vanilla SE's per-economy-tick faction-balance recovery is suppressed (FAC-187, FAC-507).
    /// The host's Harmony prefix asks this; it lives here so both server roles are tested.
    /// </summary>
    public static class VanillaBalanceRecovery
    {
        /// <summary>
        /// <para>Simulating: suppressed once the runtime's tuning is readable and says so. A top-up
        /// before that is corrected by the treasury mirror on the ledger's next movement.</para>
        /// <para>Deferred (a Nexus agent, or a server not yet holding the simulation): suppressed
        /// unless this server's own tuning turns suppression off. No mirror runs there, so a top-up
        /// that slips through is never undone, and Nexus's SaveSync can carry it to the whole cluster
        /// from the stored save. A null <paramref name="hostTuning"/> suppresses: the key's default is
        /// on.</para>
        /// </summary>
        public static bool Suppress(bool deferred, FactionTuning runtimeTuning, FactionTuning hostTuning)
        {
            if (deferred) return hostTuning == null || hostTuning.SuppressVanillaFactionBalanceRecovery;
            return runtimeTuning != null && runtimeTuning.SuppressVanillaFactionBalanceRecovery;
        }
    }
}
