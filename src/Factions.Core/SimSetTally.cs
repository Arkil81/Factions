using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// What the server is simulating, summed per party: the inputs the load governor's cost model
    /// reads (docs/load-governor.md, build step 2).
    ///
    /// <para><b>Attribution is by owner</b> (operator, 2026-09-23): a grid with NPC provenance is
    /// charged to Factions, a player's grid to that player, and anything else to
    /// <see cref="Unowned"/>. Which player is standing next to a grid does not enter into it.</para>
    ///
    /// <para>Only simulated grids are summed per party. A grid SE is not stepping costs nothing to
    /// charge, so it is only counted.</para>
    /// </summary>
    public sealed class SimSetTally
    {
        public const string Factions = "factions";
        public const string Unowned = "unowned";

        public sealed class Party
        {
            public int Grids;
            public int Awake;
            public int Moving;
            public long Blocks;
            public long Pcu;
            public long Shapes;
            public double MassTons;
        }

        private readonly Dictionary<string, Party> _parties = new Dictionary<string, Party>(StringComparer.Ordinal);

        public int SimulatedGrids { get; private set; }
        public int UnsimulatedGrids { get; private set; }

        public IEnumerable<KeyValuePair<string, Party>> Parties { get { return _parties; } }
        public int PartyCount { get { return _parties.Count; } }

        /// <summary>The party a grid is charged to.</summary>
        /// <param name="npcProvenance">The census found an NPC signal: a fleet record, a station, or
        /// an NPC-spawn mark. That wins over ownership, because a single stray player-owned block is
        /// enough to put a player in an NPC grid's <c>BigOwners</c>.</param>
        /// <param name="playerOwner">The dominant player owner's name, or null when no player is
        /// among the dominant owners.</param>
        public static string PartyFor(bool npcProvenance, string playerOwner)
        {
            if (npcProvenance) return Factions;
            return string.IsNullOrEmpty(playerOwner) ? Unowned : playerOwner;
        }

        public void Add(string party, bool simulated, int blocks, long pcu, long shapes,
                        bool awake, bool moving, double massTons)
        {
            if (!simulated)
            {
                UnsimulatedGrids++;
                return;
            }
            SimulatedGrids++;
            string key = string.IsNullOrEmpty(party) ? Unowned : party;
            Party p;
            if (!_parties.TryGetValue(key, out p))
            {
                p = new Party();
                _parties[key] = p;
            }
            p.Grids++;
            if (awake) p.Awake++;
            if (moving) p.Moving++;
            p.Blocks += blocks;
            p.Pcu += pcu;
            p.Shapes += shapes;
            p.MassTons += massTons;
        }

        public bool TryGet(string party, out Party p)
        {
            return _parties.TryGetValue(party ?? "", out p);
        }

        public void Clear()
        {
            _parties.Clear();
            SimulatedGrids = 0;
            UnsimulatedGrids = 0;
        }
    }
}
