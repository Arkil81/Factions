using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Fleets whose last grid landed where no player could see it and was closed for it, and where
    /// the next one would land (FAC-512).
    ///
    /// <para>A fleet materialises when its sim position is inside a player's observation radius; its
    /// grid is closed when the <b>grid</b> is outside that radius plus the despawn margin. When the
    /// grid lands farther from the player than the sim position said — the spawn offset, a free-place
    /// search, an order that waited in an agent's spawn queue while the player moved — the two tests
    /// disagree, and nothing changes between one spawn and the next: the sim position still passes,
    /// the grid lands in the same place and is closed again. Live 2026-11-21 on Main: one Wakizashi
    /// hauler at N096 spawned and was closed twice in 28 s, each a 3-grid hull put through a full
    /// spawn.</para>
    ///
    /// <para>So the next spawn is predicted rather than retried blind: the last landing, moved by as
    /// far as the fleet has moved since. It is let through once a player is in range of that point,
    /// and the record is dropped when the sim position leaves every player's radius (the encounter
    /// is over) or after <see cref="LatchFor"/>, whichever comes first.</para>
    /// </summary>
    public sealed class UnseenLandings
    {
        /// <summary>How long a landing record stands. A stale offset — a landing displaced by a queue
        /// wait rather than by geometry — must not keep a fleet a player is watching abstract for the
        /// rest of the pass, so it is re-tested after this.</summary>
        public static readonly TimeSpan LatchFor = TimeSpan.FromSeconds(120);

        private struct Entry
        {
            public Vec3 LandedKm;
            public Vec3 SimKm;
            public DateTime Until;
        }

        private readonly Dictionary<string, Entry> _byFleet = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public int Count { get { return _byFleet.Count; } }

        /// <summary>Records a grid closed unseen: where it was, and where the fleet's sim position was
        /// at that moment.</summary>
        public void Note(string fleetId, Vec3 landedKm, Vec3 simKm, DateTime now)
        {
            if (string.IsNullOrEmpty(fleetId)) return;
            _byFleet[fleetId] = new Entry { LandedKm = landedKm, SimKm = simKm, Until = now + LatchFor };
        }

        /// <summary>Where the next grid would land: the last landing, carried along by the fleet's
        /// movement since it was recorded.</summary>
        public static Vec3 PredictedLanding(Vec3 landedKm, Vec3 simAtCloseKm, Vec3 simNowKm)
        {
            return new Vec3(landedKm.X + (simNowKm.X - simAtCloseKm.X),
                            landedKm.Y + (simNowKm.Y - simAtCloseKm.Y),
                            landedKm.Z + (simNowKm.Z - simAtCloseKm.Z));
        }

        /// <summary>True while <paramref name="fleetId"/>'s next grid would land unseen again: no
        /// player within <paramref name="keepKm"/> of the predicted landing, as
        /// <paramref name="playerWithin"/> answers. A record that passes, or has expired, is dropped
        /// and the spawn goes ahead.</summary>
        public bool Holds(string fleetId, Vec3 simNowKm, DateTime now, Func<Vec3, double, bool> playerWithin, double keepKm)
        {
            if (_byFleet.Count == 0 || string.IsNullOrEmpty(fleetId)) return false;
            Entry e;
            if (!_byFleet.TryGetValue(fleetId, out e)) return false;
            if (now >= e.Until || playerWithin == null || playerWithin(PredictedLanding(e.LandedKm, e.SimKm, simNowKm), keepKm))
            {
                _byFleet.Remove(fleetId);
                return false;
            }
            return true;
        }

        /// <summary>The fleet's sim position left every player's radius: the next approach starts clean.</summary>
        public void Release(string fleetId)
        {
            if (_byFleet.Count == 0 || string.IsNullOrEmpty(fleetId)) return;
            _byFleet.Remove(fleetId);
        }

        /// <summary>Drops expired records, for fleets that resolved and are never asked again.</summary>
        public void Prune(DateTime now)
        {
            if (_byFleet.Count == 0) return;
            List<string> expired = null;
            foreach (KeyValuePair<string, Entry> kv in _byFleet)
                if (now >= kv.Value.Until) (expired ?? (expired = new List<string>())).Add(kv.Key);
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++) _byFleet.Remove(expired[i]);
        }
    }
}
