using System;
using System.Collections.Generic;

namespace Factions.Core
{
    /// <summary>
    /// The one exception to a station safe zone's "no building": a restore's own placement of a
    /// missing block on the ship it is restoring. Factions station zones allow only
    /// <c>LandingGearLock</c>, and SE's build request asks the zone for <c>Building</c> twice — for the
    /// grid, and for the block's box — so without this every placement inside the zone is refused
    /// while welds (which go straight to the block's integrity) pass.
    ///
    /// <para>The exception is as narrow as the call: the host enters the scope for the grids of the
    /// ship being restored around its own synchronous build request (or top-part attach) and leaves it
    /// in a <c>finally</c>. While entered, on that thread only, those grids may be built on, and a
    /// block's box may be built in. Nothing else
    /// changes: the zone's <c>AllowedActions</c> are untouched, a player's build request never runs
    /// inside the scope, and every other safe-zone question — damage, grinding, other grids — gets
    /// the zone's own answer.</para>
    /// </summary>
    public static class RestorationPlacementScope
    {
        [ThreadStatic] private static long[] _gridIds;

        /// <summary>Whether a scope is open on this thread.</summary>
        public static bool IsOpen { get { return _gridIds != null; } }

        /// <summary>Enters the scope for one grid. Dispose the result to leave; the scope never nests.</summary>
        public static IDisposable Enter(long gridEntityId)
        {
            return Enter(new[] { gridEntityId });
        }

        /// <summary>Enters the scope for every grid a restore builds on — the ship's own and the
        /// subgrids of its mechanical group, a top part it attaches included. Dispose the result to
        /// leave; the scope never nests.</summary>
        public static IDisposable Enter(IEnumerable<long> gridEntityIds)
        {
            if (gridEntityIds == null) throw new ArgumentNullException("gridEntityIds");
            var ids = new List<long>();
            foreach (long id in gridEntityIds)
            {
                if (id == 0) throw new ArgumentOutOfRangeException("gridEntityIds");
                if (!ids.Contains(id)) ids.Add(id);
            }
            if (ids.Count == 0) throw new ArgumentOutOfRangeException("gridEntityIds");
            if (_gridIds != null) throw new InvalidOperationException("A restore placement is already in progress on this thread.");
            _gridIds = ids.ToArray();
            return new Exit();
        }

        /// <summary>Whether the zone's Building check on <paramref name="entityId"/> is answered yes:
        /// only for a grid in scope.</summary>
        public static bool AllowsBuildingOn(long entityId)
        {
            long[] ids = _gridIds;
            if (ids == null || entityId == 0) return false;
            for (int i = 0; i < ids.Length; i++) if (ids[i] == entityId) return true;
            return false;
        }

        /// <summary>Whether the zone's Building check on a block's box is answered yes: only inside the
        /// scope, where the only box SE asks about is the block being placed.</summary>
        public static bool AllowsBuildingInArea()
        {
            return _gridIds != null;
        }

        private sealed class Exit : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _gridIds = null;
            }
        }
    }
}
