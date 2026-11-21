namespace Factions
{
    /// <summary>
    /// Whether this process is running the Factions simulation, readable from a static context.
    ///
    /// <para>Harmony patches are applied in <see cref="FactionsPlugin.Init"/> — before any session
    /// exists, and therefore before the Nexus authority decision is made. On a deferred server every
    /// patch site is still live. Most are inert there by data alone, but that is a property of an
    /// empty server rather than a guarantee: Nexus syncs faction and station state from the
    /// authority, so a station founded elsewhere can appear locally with no grid behind it, which is
    /// exactly the condition some of these patches act on.</para>
    ///
    /// <para>Every patch reads this instead of reasoning about that. Deferred means vanilla runs
    /// untouched.</para>
    /// </summary>
    internal static class SimulationGate
    {
        /// <summary>
        /// True when a Nexus cluster exists and another server owns the simulation. Set once at
        /// session load and cleared on unload; false on a standalone server and before session load.
        /// </summary>
        internal static volatile bool Deferred;

        /// <summary>
        /// True on a deferred server once it stands Factions stations for its own sectors from the
        /// authority's node snapshot (set when the first snapshot has seeded its claims). Patches that
        /// keep vanilla off a Factions station act while this is set, even though <see cref="Deferred"/> is.
        /// </summary>
        internal static volatile bool Hands;

        /// <summary>
        /// True on a deferred server from the moment its agent materialisers start until session
        /// unload; false if they failed to start. Between this and <see cref="Hands"/> the station
        /// cell generator spawns nothing (<see cref="Factions.Core.StationSeedGate"/>).
        /// </summary>
        internal static volatile bool HandsPending;
    }
}
