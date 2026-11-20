namespace Factions.Core
{
    /// <summary>
    /// Whether a Nexus cluster was detected around this process, and whether this server's own
    /// identity within it could be read.
    /// </summary>
    public enum NexusPresence
    {
        /// <summary>No Nexus plugin in this process: a standalone server.</summary>
        Absent = 0,

        /// <summary>Nexus is present and this server's ID was read successfully.</summary>
        Resolved = 1,

        /// <summary>Nexus is present but its server ID could not be read.</summary>
        Unresolvable = 2,
    }

    /// <summary>
    /// Decides whether this process may run the Factions simulation.
    ///
    /// On a Nexus cluster the servers share one economy: Nexus replicates faction, identity,
    /// reputation and banking state across every member, applying an absolute target balance on
    /// receipt. Two Factions instances simulating at once are therefore two writers on the same NPC
    /// accounts, not two independent worlds. Exactly one server may simulate.
    /// </summary>
    public static class NexusAuthority
    {
        /// <summary>
        /// True when this process should build its runtime and simulate.
        /// <paramref name="reason"/> is always set, and is written to the log either way.
        /// </summary>
        /// <param name="presence">What was detected about the surrounding cluster.</param>
        /// <param name="thisServerId">This server's Nexus ID; only read when <paramref name="presence"/> is Resolved.</param>
        /// <param name="authorityServerId">Configured owner of the simulation; 0 means unconfigured.</param>
        public static bool ShouldSimulate(
            NexusPresence presence, int thisServerId, int authorityServerId, out string reason)
        {
            if (presence == NexusPresence.Absent)
            {
                reason = "no Nexus in this process; simulating as a standalone server.";
                return true;
            }

            // Deferring is recoverable by editing tuning.json and restarting; simulating from an
            // unverified identity corrupts shared balances irreversibly. Both unknowns fail closed.
            if (presence == NexusPresence.Unresolvable)
            {
                reason = "Nexus is present but its server ID could not be read, so this process "
                       + "cannot prove it is the authority. Deferring.";
                return false;
            }

            if (authorityServerId <= 0)
            {
                reason = "Nexus server " + thisServerId + " detected but NexusAuthorityServerId is "
                       + "unset in tuning.json, so no server owns the simulation. Deferring. Set it "
                       + "to " + thisServerId + " on exactly one server in the cluster.";
                return false;
            }

            if (thisServerId != authorityServerId)
            {
                reason = "Nexus server " + thisServerId + " is not the configured authority ("
                       + authorityServerId + "). Deferring.";
                return false;
            }

            reason = "Nexus server " + thisServerId + " is the configured authority; simulating.";
            return true;
        }

        /// <summary>
        /// What this process does for the cluster, given the presence probe and the
        /// <see cref="ShouldSimulate"/> decision. An agent relays client uplinks to the authority
        /// and needs its own server ID for the reply address, so an unreadable identity yields
        /// <see cref="NexusRole.Dark"/> rather than an agent that cannot say where it is.
        /// </summary>
        public static NexusRole RoleFor(NexusPresence presence, bool simulating)
        {
            if (presence == NexusPresence.Absent) return NexusRole.Standalone;
            if (simulating) return NexusRole.Authority;
            return presence == NexusPresence.Resolved ? NexusRole.Agent : NexusRole.Dark;
        }
    }

    /// <summary>What a process does for a Nexus cluster.</summary>
    public enum NexusRole
    {
        /// <summary>No cluster: simulates, serves its own clients, relays nothing.</summary>
        Standalone = 0,

        /// <summary>Simulates, serves local clients, and serves remote clients relayed by agents.</summary>
        Authority = 1,

        /// <summary>Does not simulate. Answers its own clients' uplinks and relays their feature
        /// traffic to the authority.</summary>
        Agent = 2,

        /// <summary>Does not simulate and cannot relay: Nexus is present but this server's ID is
        /// unreadable, so there is no reply address to give the authority.</summary>
        Dark = 3,
    }
}
