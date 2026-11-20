using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>A player on another cluster member, as that member last reported them.</summary>
    public struct RemotePlayer
    {
        public ulong SteamId;
        public long IdentityId;
        /// <summary>World position in metres.</summary>
        public Vec3 PositionM;
        /// <summary>Velocity in m/s.</summary>
        public Vec3 Velocity;
        /// <summary>The grid the player sits at a controller of on their server; 0 when not seated.</summary>
        public long SeatGridId;
        /// <summary>That grid's world position in metres.</summary>
        public Vec3 GridPositionM;
        /// <summary>That grid's sensor strength in km, read from its antennas on the player's server.</summary>
        public double SensorKm;
        public int AntennaCount;
        /// <summary>The Nexus id of the server that reported this player.</summary>
        public byte HomeServerId;
        public DateTime ReceivedUtc;
    }

    /// <summary>
    /// The authority's view of where the players on the other cluster members are. Each member
    /// reports its whole set on a cadence; a report replaces that member's previous one, and a
    /// member that stops reporting ages out whole, so a crashed or partitioned server cannot leave
    /// players standing where they were.
    ///
    /// <para>A player who moved servers can briefly appear in two reports. <see cref="Snapshot"/>
    /// keeps the newer, and drops anyone the caller says is connected locally.</para>
    /// </summary>
    public sealed class RemotePlayerPositions
    {
        private sealed class Report
        {
            public DateTime ReceivedUtc;
            public List<RemotePlayer> Players;
        }

        private readonly Dictionary<byte, Report> _byHome = new Dictionary<byte, Report>();

        /// <summary>Members with a report on hand, expired or not.</summary>
        public int HomeCount { get { return _byHome.Count; } }

        /// <summary>The members with a report on hand, in id order.</summary>
        public List<byte> Homes()
        {
            var homes = new List<byte>(_byHome.Keys);
            homes.Sort();
            return homes;
        }

        /// <summary>Replaces <paramref name="homeServerId"/>'s report. Returns true when that member
        /// had no report on hand, which is when it starts (or resumes) reporting.</summary>
        public bool Update(byte homeServerId, IList<RemotePlayer> players, DateTime nowUtc)
        {
            bool isNew = !_byHome.ContainsKey(homeServerId);
            var list = new List<RemotePlayer>(players == null ? 0 : players.Count);
            if (players != null)
            {
                for (int i = 0; i < players.Count; i++)
                {
                    RemotePlayer p = players[i];
                    p.HomeServerId = homeServerId;
                    p.ReceivedUtc = nowUtc;
                    list.Add(p);
                }
            }
            _byHome[homeServerId] = new Report { ReceivedUtc = nowUtc, Players = list };
            return isNew;
        }

        /// <summary>Drops every member whose last report is older than <paramref name="maxAge"/>
        /// and returns their ids; empty when none expired.</summary>
        public List<byte> Expire(DateTime nowUtc, TimeSpan maxAge)
        {
            var expired = new List<byte>();
            foreach (KeyValuePair<byte, Report> kv in _byHome)
                if (nowUtc - kv.Value.ReceivedUtc > maxAge) expired.Add(kv.Key);
            for (int i = 0; i < expired.Count; i++) _byHome.Remove(expired[i]);
            return expired;
        }

        /// <summary>Every reported player whose member reported within <paramref name="maxAge"/>,
        /// one entry per SteamID (the newest report wins), excluding <paramref name="localSteamIds"/>.</summary>
        public List<RemotePlayer> Snapshot(DateTime nowUtc, TimeSpan maxAge, ICollection<ulong> localSteamIds)
        {
            var newest = new Dictionary<ulong, RemotePlayer>();
            foreach (KeyValuePair<byte, Report> kv in _byHome)
            {
                if (nowUtc - kv.Value.ReceivedUtc > maxAge) continue;
                List<RemotePlayer> players = kv.Value.Players;
                for (int i = 0; i < players.Count; i++)
                {
                    RemotePlayer p = players[i];
                    if (localSteamIds != null && localSteamIds.Contains(p.SteamId)) continue;
                    RemotePlayer held;
                    if (newest.TryGetValue(p.SteamId, out held) && held.ReceivedUtc >= p.ReceivedUtc) continue;
                    newest[p.SteamId] = p;
                }
            }
            return new List<RemotePlayer>(newest.Values);
        }

        /// <summary>Per member: id, players in its last report, and that report's age in seconds.
        /// For an operator readback.</summary>
        public string Describe(DateTime nowUtc)
        {
            if (_byHome.Count == 0) return "none";
            var parts = new List<string>();
            var homes = new List<byte>(_byHome.Keys);
            homes.Sort();
            for (int i = 0; i < homes.Count; i++)
            {
                Report r = _byHome[homes[i]];
                parts.Add("server" + homes[i] + "=" + r.Players.Count + "p/"
                          + ((int)(nowUtc - r.ReceivedUtc).TotalSeconds) + "s");
            }
            return string.Join(" ", parts.ToArray());
        }
    }
}
