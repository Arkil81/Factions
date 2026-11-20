using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>Why one mechanical group near a fight was, or was not, closed by the fight sweep.</summary>
    public enum FightDebrisVerdict
    {
        Close,
        /// <summary>None of the three sim-born carriers: not the simulation's to close.</summary>
        NotSimBorn,
        /// <summary>Attached to a hull a fleet is still flying.</summary>
        Held,
        Station,
        Respawn,
        /// <summary>An unmarked member belongs to a player.</summary>
        PlayerOwned,
        /// <summary>A member's owner faction is no longer the faction it was marked for: a player took it.</summary>
        Captured,
        /// <summary>Farther than <see cref="FightDebris.RadiusKm"/> from every fight site.</summary>
        OutOfRange,
        /// <summary>Broke off less than <see cref="FightDebris.SettleSeconds"/> ago.</summary>
        Settling,
        /// <summary>A wreck-sized piece inside its salvage window.</summary>
        Salvage
    }

    /// <summary>The facts the fight sweep judges one group by: the reaper's own description of it, plus
    /// what only a fight knows.</summary>
    public struct FightDebrisFacts
    {
        public ReapCandidate Group;
        public bool Captured;
        /// <summary>Distance to the nearest fight site, km.</summary>
        public double DistanceKm;
    }

    /// <summary>
    /// What a materialised fight leaves behind is cleared while the fight is still on
    /// (docs/fight-control.md, Debris). The world reaper is built for litter nobody is watching: it runs
    /// every 300 s, waits 600 s with a player near, and closes 50 groups a pass. Live 2026-09-29 one fight
    /// left 100-200 loose grids moving in gravity for minutes (FAC-425).
    ///
    /// <para>Small pieces go once they have stopped being an explosion; a wreck-sized piece keeps a
    /// salvage window first (operator, 2026-10-07), which the sim floor cancels.</para>
    /// </summary>
    public static class FightDebris
    {
        /// <summary>How often the sweep runs while any site is live.</summary>
        public const double SweepSeconds = 10;
        /// <summary>How far from a fight site a piece is still the fight's.</summary>
        public const double RadiusKm = 5;
        /// <summary>How long a piece is left after it broke off before it is closed.</summary>
        public const double SettleSeconds = 10;
        /// <summary>A piece of at least this many blocks is a wreck, and keeps a salvage window.</summary>
        public const int WreckMinBlocks = 50;
        /// <summary>The salvage window, from when the piece broke off.</summary>
        public const double WreckSalvageSeconds = 120;
        /// <summary>How long a site is kept after its last combat hull left.</summary>
        public const double SiteGraceSeconds = 300;
        /// <summary>Two hull positions closer than this are one site.</summary>
        public const double SiteMergeKm = 1.0;
        /// <summary>Groups closed per pass at most; the rest wait for the next.</summary>
        public const int MaxGroupsPerPass = 40;
        /// <summary>Below this sim ratio a wreck's salvage window is skipped.</summary>
        public const double SimFloor = 0.8;

        public static FightDebrisVerdict Judge(FightDebrisFacts f, bool simFloorTripped)
        {
            ReapCandidate c = f.Group;
            if (!c.Marked && !c.Bit && !c.NamedSimBorn) return FightDebrisVerdict.NotSimBorn;
            if (c.Held)                return FightDebrisVerdict.Held;
            if (c.Station)             return FightDebrisVerdict.Station;
            if (c.Respawn)             return FightDebrisVerdict.Respawn;
            if (c.UnmarkedPlayerOwned) return FightDebrisVerdict.PlayerOwned;
            if (f.Captured)            return FightDebrisVerdict.Captured;
            if (!(f.DistanceKm <= RadiusKm)) return FightDebrisVerdict.OutOfRange;

            // AgeSeconds is MaxValue for a group no record of this process dates: it came back from the
            // save, so it is older than any window here.
            if (c.AgeSeconds < SettleSeconds) return FightDebrisVerdict.Settling;
            if (c.Blocks >= WreckMinBlocks && !simFloorTripped && c.AgeSeconds < WreckSalvageSeconds)
                return FightDebrisVerdict.Salvage;
            return FightDebrisVerdict.Close;
        }
    }

    /// <summary>Where fights are, and were. Fed the positions of every materialised combat hull on each
    /// sweep; a site lives on for <see cref="FightDebris.SiteGraceSeconds"/> after the last hull near it
    /// left, because the wreckage outlives the fight.</summary>
    public sealed class FightSiteMemory
    {
        private struct Site
        {
            public Vec3 PosKm;
            public DateTime UntilUtc;
        }

        private readonly List<Site> _sites = new List<Site>();

        public int Count { get { return _sites.Count; } }

        /// <summary>Refreshes the sites from <paramref name="hullsKm"/> and drops expired ones.</summary>
        public void Update(IList<Vec3> hullsKm, DateTime nowUtc)
        {
            DateTime until = nowUtc.AddSeconds(FightDebris.SiteGraceSeconds);
            if (hullsKm != null)
            {
                for (int h = 0; h < hullsKm.Count; h++)
                {
                    Vec3 p = hullsKm[h];
                    int near = -1;
                    for (int s = 0; s < _sites.Count; s++)
                        if (_sites[s].PosKm.DistanceTo(p) <= FightDebris.SiteMergeKm) { near = s; break; }
                    if (near >= 0) _sites[near] = new Site { PosKm = _sites[near].PosKm, UntilUtc = until };
                    else _sites.Add(new Site { PosKm = p, UntilUtc = until });
                }
            }
            _sites.RemoveAll(s => s.UntilUtc <= nowUtc);
        }

        /// <summary>Distance from <paramref name="posKm"/> to the nearest site, or
        /// <see cref="double.PositiveInfinity"/> when there is none.</summary>
        public double NearestKm(Vec3 posKm)
        {
            double best = double.PositiveInfinity;
            for (int s = 0; s < _sites.Count; s++)
            {
                double d = _sites[s].PosKm.DistanceTo(posKm);
                if (d < best) best = d;
            }
            return best;
        }
    }
}
