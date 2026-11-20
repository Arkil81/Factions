using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Runtime-only switches, flipped with 'factions feature' and never persisted. Not tuning, and
    /// not a holding pen for finished features: on 2026-10-09 every feature switch was removed with its
    /// off path and its behaviour made unconditional (operator: "flip all the implemented features to ON
    /// and remove their switches"); what each still owes is a row in docs/live-state.md. What remains here
    /// is a diagnostic that changes the thing it measures, so it can never be left on, and new behaviour
    /// that could destabilise the simulation or cause damage, past its first deploy, on at boot until its
    /// ticket closes and the switch goes. Nothing else gets a switch (AGENTS.md rule 8).</summary>
    public static class FeatureSwitch
    {
        /// <summary>FAC-445 diagnostic: SE's stepped physics worlds are stepped one after another and timed, instead of
        /// in one parallel job, so a stalled step names its cluster. Changes the scheduling it measures.</summary>
        public const string PhysicsPerWorld = "physics-per-world";

        /// <summary>A materialised station stands in the asteroids its vanilla spawn group places around it.
        /// On at boot from its second deploy (AGENTS.md rule 8).</summary>
        public const string StationVoxels = "station-voxels";

        /// <summary>A materialised station's tagged containers hold loot drawn from its node's stock, and
        /// vanilla's free container fill stays off it. On at boot from its second deploy (AGENTS.md rule 8).</summary>
        public const string StationLoot = "station-loot";

        /// <summary>Enemy NPC fleets in a faction's space are marked as intruders and, at Intercept, caught by
        /// the faction's Guard at the node (the FleetTrespass class). Off-screen hulls are lost and cargo
        /// taken across the whole world, so it is switched (AGENTS.md rule 8): off on its first deploy (v0.550.0,
        /// switched on by the operator 2026-10-17), on at boot from the next. Also gates route pricing
        /// (TransitRisk).</summary>
        public const string FleetTrespass = "fleet-trespass";

        public static readonly string[] Known = { PhysicsPerWorld, StationVoxels, StationLoot, FleetTrespass };

        private static readonly Dictionary<string, bool> State =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { StationVoxels, true },
                { StationLoot, true },
                { FleetTrespass, true },
            };

        public static bool IsOn(string name)
        {
            bool on;
            lock (State) return State.TryGetValue(name, out on) && on;
        }

        /// <summary>False when <paramref name="name"/> is not a known switch.</summary>
        public static bool Set(string name, bool on)
        {
            if (Array.IndexOf(Known, name) < 0) return false;
            lock (State) State[name] = on;
            return true;
        }
    }
}
