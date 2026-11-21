using System;
using System.Collections.Generic;
using Sandbox.Game.World;
using VRageMath;

namespace Factions
{
    /// <summary>One client a feature manager serves: who it is and which identity it plays.</summary>
    internal struct LinkedViewer
    {
        public ulong SteamId;
        public long IdentityId;
    }

    /// <summary>
    /// The players a feature manager serves. That is not the same set as
    /// <c>MySession.Players.GetOnlinePlayers()</c>. On a Nexus authority, a client relayed from an
    /// agent is linked and subscribed but is not online here, so a loop over the local roster
    /// skips it, and its map, events, assets and every other stream never send. The client then
    /// shows "waiting for server" for as long as it stays linked.
    ///
    /// <para>The identity of a relayed client resolves through the session's player table, which
    /// holds every player this shard has saved, connected or not. A player it has not met yet
    /// falls back to the identity their agent reports with their position.</para>
    ///
    /// <para>Only for managers that need nothing but the SteamID and the identity. Anything that
    /// reads the character, the controlled grid or the position is local by nature. A relayed
    /// client's entities live on its agent.</para>
    /// </summary>
    internal static class LinkedViewers
    {
        /// <summary>Set by <see cref="UplinkManager"/> while it is the live one.</summary>
        internal static UplinkManager Uplink;

        /// <summary>Authority only: the identity an agent's position report names for a player it
        /// hosts. The fallback for a player this session's own table has not met yet.</summary>
        internal static Func<ulong, long> ReportedIdentity;

        /// <summary>Where a player connected to a Nexus agent stands, and which agent: from that
        /// agent's position report.</summary>
        internal delegate bool RemotePositionLookup(ulong steamId, out Vector3D worldM, out byte home);

        /// <summary>Authority only: set while agents report. False when no current report names the
        /// player.</summary>
        internal static RemotePositionLookup RemotePosition;

        /// <summary>A relayed client's position by its agent's report; false for a player connected
        /// here, or one no current report names.</summary>
        public static bool TryRemotePosition(ulong steamId, out Vector3D worldM, out byte home)
        {
            worldM = Vector3D.Zero;
            home = 0;
            UplinkManager uplink = Uplink;
            RemotePositionLookup lookup = RemotePosition;
            if (uplink == null || lookup == null || !uplink.IsRemote(steamId)) return false;
            return lookup(steamId, out worldM, out home);
        }

        private static readonly List<LinkedViewer> Scratch = new List<LinkedViewer>();
        private static readonly HashSet<ulong> Seen = new HashSet<ulong>();
        private static readonly List<ulong> Remote = new List<ulong>();

        /// <summary>
        /// Local online players, then each relayed client whose identity this session knows. The
        /// returned list is reused by the next call, so a caller must finish iterating before it
        /// calls again. That always holds here: every manager runs on the game thread.
        /// </summary>
        public static List<LinkedViewer> Collect()
        {
            Scratch.Clear();
            Seen.Clear();
            MySession session = MySession.Static;
            if (session == null || session.Players == null) return Scratch;

            foreach (MyPlayer player in session.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Identity == null) continue;
                if (!Seen.Add(player.Id.SteamId)) continue;
                Scratch.Add(new LinkedViewer { SteamId = player.Id.SteamId, IdentityId = player.Identity.IdentityId });
            }

            UplinkManager uplink = Uplink;
            if (uplink == null) return Scratch;
            Remote.Clear();
            uplink.AddRemoteTo(Remote);
            foreach (ulong steamId in Remote)
            {
                if (Seen.Contains(steamId)) continue;
                long identityId = SessionIdentity(session, steamId);
                if (identityId == 0) continue;
                Seen.Add(steamId);
                Scratch.Add(new LinkedViewer { SteamId = steamId, IdentityId = identityId });
            }
            return Scratch;
        }

        /// <summary>The identity a client plays, whether it is online here or relayed from an
        /// agent; 0 when this session knows neither.</summary>
        public static long IdentityFor(ulong steamId)
        {
            MySession session = MySession.Static;
            if (session == null || session.Players == null || steamId == 0) return 0L;
            foreach (MyPlayer player in session.Players.GetOnlinePlayers())
            {
                if (player == null || player.IsBot || player.Identity == null) continue;
                if (player.Id.SteamId == steamId) return player.Identity.IdentityId;
            }
            UplinkManager uplink = Uplink;
            if (uplink == null || !uplink.IsRemote(steamId)) return 0L;
            return SessionIdentity(session, steamId);
        }

        private static long SessionIdentity(MySession session, ulong steamId)
        {
            long identityId = 0L;
            try { identityId = session.Players.TryGetIdentityId(steamId); }
            catch { identityId = 0L; }
            if (identityId != 0L) return identityId;
            Func<ulong, long> reported = ReportedIdentity;
            return reported != null ? reported(steamId) : 0L;
        }
    }
}
