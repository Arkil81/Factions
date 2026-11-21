using System;
using System.Collections.Generic;
using Factions.Core;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Watches fleet grids crossing a Nexus sector boundary, on every server of a cluster.
    ///
    /// <para><b>What Nexus does.</b> Its boundary scan sends a grid group whose bounding-box centre lies
    /// in another server's sector as one object-builder message and closes it on the source in the
    /// same frame, unacknowledged. The target spawns it with its EntityId and entity name intact
    /// (read in the Nexus v3 decompile, 2026-11-20: <c>GridTransportMessage.StartTransfer</c>,
    /// <c>TransportSync.RequestGridTransfer</c>, <c>ParallelSpawner</c>). So a fleet grid leaves as
    /// an ordinary close here and arrives there as a grid nothing holds, still named
    /// <c>factions:npc:&lt;eid&gt;:&lt;key&gt;</c>.</para>
    ///
    /// <para><b>What this records.</b> A named grid removed at a position another server's sector
    /// holds is <i>left</i> for that server, and every named grid added is an <i>arrival</i>. Whether
    /// a removal was this process's own close, and whether an arrival is a fleet grid, is decided by
    /// the reader: <see cref="AgentFleets"/> on an agent, <see cref="VanillaFleetSpawner"/> and the
    /// plugin on the authority. Attached once the world has loaded, so a boot's saved grids are not
    /// arrivals.</para>
    ///
    /// <para>Game thread only, like everything that touches <c>MyEntities</c>.</para>
    /// </summary>
    internal static class SectorHandover
    {
        /// <summary>How long a departure or arrival waits for its reader.</summary>
        private static readonly TimeSpan Kept = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

        private struct Left
        {
            public byte To;
            public string Key;
            public DateTime Utc;
        }

        private static readonly Dictionary<long, Left> _left = new Dictionary<long, Left>();
        private static readonly Dictionary<long, DateTime> _arrived = new Dictionary<long, DateTime>();
        private static readonly List<long> _scratch = new List<long>();
        private static byte _own;
        private static bool _attached;

        public static bool Attached { get { return _attached; } }

        public static void Attach(byte ownServer)
        {
            _own = ownServer;
            if (_attached) return;
            MyEntities.OnEntityAdd += OnAdd;
            MyEntities.OnEntityRemove += OnRemove;
            _attached = true;
        }

        public static void Detach()
        {
            if (_attached)
            {
                MyEntities.OnEntityAdd -= OnAdd;
                MyEntities.OnEntityRemove -= OnRemove;
            }
            _attached = false;
            _left.Clear();
            _arrived.Clear();
        }

        /// <summary>The server a named grid left for, and the fleet key its name carried, taken once.</summary>
        public static bool TryTakeLeft(long eid, out byte to, out string fleetKey)
        {
            to = 0;
            fleetKey = null;
            Left l;
            if (!_left.TryGetValue(eid, out l)) return false;
            _left.Remove(eid);
            if (DateTime.UtcNow - l.Utc > Kept) return false;
            to = l.To;
            fleetKey = l.Key;
            return true;
        }

        /// <summary>The named grids added at least <see cref="Settle"/> ago and still standing, taken
        /// once. A rotor top links to its hull a frame or more after it is added, and the reader asks
        /// which grid of the group is the hull.</summary>
        public static void TakeArrivals(List<long> into)
        {
            into.Clear();
            if (_arrived.Count == 0) return;
            DateTime now = DateTime.UtcNow;
            foreach (KeyValuePair<long, DateTime> kv in _arrived)
                if (now - kv.Value >= Settle) into.Add(kv.Key);
            foreach (long eid in into) _arrived.Remove(eid);
            Prune(now);
        }

        private static void Prune(DateTime now)
        {
            if (_left.Count == 0) return;
            _scratch.Clear();
            foreach (KeyValuePair<long, Left> kv in _left) if (now - kv.Value.Utc > Kept) _scratch.Add(kv.Key);
            foreach (long eid in _scratch) _left.Remove(eid);
            _scratch.Clear();
        }

        private static bool IsFleetGrid(MyCubeGrid grid, out string fleetKey)
        {
            fleetKey = null;
            long namedEid;
            return grid != null && NpcGridNaming.TryParseName(grid.Name, out namedEid, out fleetKey) && namedEid == grid.EntityId;
        }

        private static void OnAdd(MyEntity entity)
        {
            var grid = entity as MyCubeGrid;
            string key;
            if (!IsFleetGrid(grid, out key)) return;
            _arrived[grid.EntityId] = DateTime.UtcNow;
        }

        private static void OnRemove(MyEntity entity)
        {
            var grid = entity as MyCubeGrid;
            string key;
            if (!IsFleetGrid(grid, out key)) return;
            _arrived.Remove(grid.EntityId);
            byte to;
            // Where Nexus judged it: the centre of its world bounding box.
            if (!NexusGateway.TryServerAt(grid.PositionComp.WorldAABB.Center, out to) || to == 0 || to == _own) return;
            _left[grid.EntityId] = new Left { To = to, Key = key, Utc = DateTime.UtcNow };
        }
    }
}
