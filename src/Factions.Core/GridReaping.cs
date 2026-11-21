using System;

namespace Factions.Core
{
    /// <summary>Why one mechanical group was, or was not, reaped. The refusal cases are the whole
    /// diagnostic channel for the reaper: a pass that closes nothing and a pass with nothing to
    /// close are the two outcomes an operator has to be able to tell apart.</summary>
    public enum ReapVerdict
    {
        /// <summary>Close it.</summary>
        Reap,
        /// <summary>Nothing in the group carries the record or the entity name, so it is not the
        /// simulation's to close. A player's grid lands here, and so does a grid SE spawned for
        /// itself: a Search, Escort, Repair, Salvage or bounty contract's target, a container drop,
        /// an encounter.</summary>
        NotSimBorn,
        /// <summary>A fleet still means to fly some grid in this group.</summary>
        Held,
        /// <summary>A founded station's grid, or a grid SE generated.</summary>
        Station,
        /// <summary>A respawn pod or respawn ship.</summary>
        Respawn,
        /// <summary>An <b>unmarked</b> member belongs to a non-NPC identity — a player docked to, or
        /// building on, a convoy. The marked grids are ours, but the assembly is not ours to close.</summary>
        PlayerOwned,
        /// <summary>Marked inside the grace window with a player close enough to be watching it.</summary>
        TooYoung
    }

    /// <summary>One mechanical group, flattened to the facts the reap rule is written in terms of.
    /// The host builds it from <c>MyCubeGrid</c>; nothing here knows what a grid is.
    ///
    /// <para><b>The group is the unit, not the grid.</b> A rotor-mounted turret is its own grid, so
    /// judging grids one at a time would reap the turrets off a hull a fleet is still flying, and
    /// would close half of an assembly a player is docked to.</para></summary>
    public struct ReapCandidate
    {
        /// <summary>The group's root grid, the id an operator can reach with <c>factions closegrid</c>.</summary>
        public long EntityId;

        /// <summary>Some member carries this process's <see cref="SimGridRecord"/>.</summary>
        public bool Marked;

        /// <summary>Some member carries <c>IsNpcSpawnedGrid</c>. Not evidence the simulation spawned
        /// it, and <see cref="GridReaping.Judge"/> does not read it: SE sets the same bit on every
        /// grid it spawns for an NPC — contract targets, container drops, encounters — and every
        /// grid this plugin marks also carries the entity name. Kept for the pass report.</summary>
        public bool Bit;

        /// <summary>Some member carries the <c>factions:npc:</c> entity name, or a record rebuilt
        /// from one. The carrier SE does not own, and the one that answers after a restart for a
        /// grid whose bit has been cleared.</summary>
        public bool NamedSimBorn;

        /// <summary>A fleet holds some member: a materialised lead, an escort, a stashed squadron,
        /// or a grid whose record names a fleet that is still pending.</summary>
        public bool Held;

        public bool Station;
        public bool Respawn;

        /// <summary>Some member that carries none of the three sim-born carriers has a non-NPC
        /// identity among its owners. The mark trumps the owner on the marked members themselves: a player
        /// faction's convoy is owned by the player's identity by construction.</summary>
        public bool UnmarkedPlayerOwned;

        /// <summary>A marked member is owned by a faction other than the one it was marked for: a player
        /// took the hull. The mark says it was ours; the owner says it no longer is.</summary>
        public bool Captured;

        /// <summary>Seconds since the youngest member was marked in this process.
        /// <see cref="double.MaxValue"/> when no member carries a record this process wrote — a grid
        /// identified by its bit or its name survived a restart, so it is older than any grace this
        /// process could measure.</summary>
        public double AgeSeconds;

        /// <summary>A player is within observation range of the group.</summary>
        public bool PlayerNear;

        /// <summary>Blocks across the whole group, for the pass totals.</summary>
        public int Blocks;
    }

    /// <summary>The reap rule, as a function of the facts alone.</summary>
    public static class GridReaping
    {
        /// <summary>Whether <paramref name="c"/> is the simulation's litter, and if not, what saved it.
        ///
        /// <para>The age term is a disjunction on purpose: a missile in a fight someone is watching
        /// is content rather than litter, so the grace protects it — but with nobody in range there
        /// is nothing for the grace to protect, and a grid nobody can see is reaped the moment it is
        /// unheld.</para></summary>
        public static ReapVerdict Judge(ReapCandidate c, double graceSeconds)
        {
            if (!c.Marked && !c.NamedSimBorn) return ReapVerdict.NotSimBorn;
            if (c.Held)                  return ReapVerdict.Held;
            if (c.Station)               return ReapVerdict.Station;
            if (c.Respawn)               return ReapVerdict.Respawn;
            if (c.UnmarkedPlayerOwned || c.Captured) return ReapVerdict.PlayerOwned;
            if (c.AgeSeconds < graceSeconds && c.PlayerNear) return ReapVerdict.TooYoung;
            return ReapVerdict.Reap;
        }

        /// <summary>The name bucket a grid falls in for the census: its display name with SE's
        /// auto-numbering suffix removed, so the 158 grids SE called <c>Small Grid 1234</c> read as
        /// one row. A name that is nothing but digits keeps them — that is a name someone chose.</summary>
        public static string NameBucket(string displayName)
        {
            string name = displayName == null ? "" : displayName.Trim();
            if (name.Length == 0) return "(unnamed)";

            int end = name.Length;
            while (end > 0 && name[end - 1] >= '0' && name[end - 1] <= '9') end--;
            if (end == name.Length || end == 0) return name;          // no suffix, or all digits
            if (name[end - 1] != ' ') return name;                    // digits welded to the name

            string stem = name.Substring(0, end - 1).TrimEnd();
            return stem.Length == 0 ? name : stem;
        }
    }
}
