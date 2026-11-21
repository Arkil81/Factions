namespace Factions.Core
{
    /// <summary>What the station cell-generator prefix does with SE's station seeds.</summary>
    public enum StationSeedPolicy
    {
        /// <summary>Leave the list alone: SE spawns every station seed it reaches.</summary>
        Vanilla,
        /// <summary>Remove the seeds of Factions stations; SE spawns the rest.</summary>
        WithholdOurs,
        /// <summary>Remove every station seed; SE spawns none.</summary>
        WithholdAll,
    }

    /// <summary>
    /// Which <see cref="StationSeedPolicy"/> applies, from the server's Nexus role and how far an agent
    /// has got through its start.
    ///
    /// <para><b>An agent before its first node snapshot withholds every seed.</b> An agent knows which
    /// stations are Factions' only once the authority's snapshot has seeded its claims, 10-30 s after
    /// boot. A player connected inside that window used to get SE's own station grids spawned around
    /// them, saved with the sector and back on every restart: seven on Main on 2026-11-20, at the
    /// 17:02:51Z and 17:45:12Z rejoins (claims seeded 17:03:07Z and 17:45:22Z). Every station record
    /// in that world belongs to a Factions faction, so there is nothing for SE to place there.</para>
    ///
    /// <para><b>A deferred server with no agent hands stays vanilla.</b> That is a server whose agent
    /// start failed, or one deferred for an unusable data directory: nobody there stands Factions
    /// stations, so withholding SE's would leave its sectors with none.</para>
    /// </summary>
    public static class StationSeedGate
    {
        /// <param name="deferred">Another server owns the simulation, or this one may not run it.</param>
        /// <param name="hands">This agent's station claims are seeded from a snapshot.</param>
        /// <param name="handsPending">This agent started its materialisers and awaits that snapshot.</param>
        public static StationSeedPolicy For(bool deferred, bool hands, bool handsPending)
        {
            if (!deferred || hands) return StationSeedPolicy.WithholdOurs;
            return handsPending ? StationSeedPolicy.WithholdAll : StationSeedPolicy.Vanilla;
        }
    }
}
