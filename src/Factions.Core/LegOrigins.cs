using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// Where each materialised grid was when it was first seen on its current leg, so a stall verdict
    /// can tell a grid that never got under way from one that flew and then stopped. A leg is named
    /// by its departure time: a grid retargeted in place onto its next leg is re-originated where it
    /// sits, and a grid spawned part-way along a leg is originated where it appeared. Entries not
    /// observed for the prune age are dropped, so a grid that left the world without being forgotten
    /// costs nothing for long, while one skipped for a frame or two keeps its origin.
    /// </summary>
    public sealed class LegOrigins
    {
        private struct Entry
        {
            public DateTime LegDepartedUtc;
            public Vec3 OriginKm;
            public DateTime LastSeenUtc;
        }

        private readonly Dictionary<long, Entry> _byGrid = new Dictionary<long, Entry>();
        private readonly List<long> _expired = new List<long>();

        public int Count { get { return _byGrid.Count; } }

        /// <summary>Records one observation of grid <paramref name="eid"/> at <paramref name="posKm"/>
        /// on the leg that departed at <paramref name="legDepartedUtc"/>, and returns how far it is
        /// from where it was first seen on that leg. The first observation of a leg returns 0.</summary>
        public double Observe(long eid, DateTime legDepartedUtc, Vec3 posKm, DateTime now)
        {
            Entry e;
            if (!_byGrid.TryGetValue(eid, out e) || e.LegDepartedUtc != legDepartedUtc)
            {
                e.LegDepartedUtc = legDepartedUtc;
                e.OriginKm = posKm;
            }
            e.LastSeenUtc = now;
            _byGrid[eid] = e;
            return posKm.DistanceTo(e.OriginKm);
        }

        public void Forget(long eid)
        {
            _byGrid.Remove(eid);
        }

        /// <summary>Drops every grid not observed within <paramref name="maxAge"/> of
        /// <paramref name="now"/>.</summary>
        public void Prune(DateTime now, TimeSpan maxAge)
        {
            if (_byGrid.Count == 0) return;
            _expired.Clear();
            foreach (KeyValuePair<long, Entry> kv in _byGrid)
                if (now - kv.Value.LastSeenUtc > maxAge) _expired.Add(kv.Key);
            for (int i = 0; i < _expired.Count; i++) _byGrid.Remove(_expired[i]);
        }
    }
}
