using System;
using System.Collections.Generic;
using System.Reflection;
using NLog;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;

namespace Factions
{
    /// <summary>
    /// Clears the blueprint a spawned NPC hull's projector carries, before its first update builds it
    /// (operator, 2026-09-25).
    ///
    /// <para><b>The hitch it removes.</b> The hitch probe's first read (2026-09-25 09:29-09:40Z) put
    /// the three worst frames on the three Naginata spawns to the second: 420, 367 and 257 ms, with
    /// <c>MySpaceProjector</c> taking 96-171 ms of each in one update. <c>MyProjectorBase.Init</c>
    /// keeps the prefab's <c>ProjectedGrids</c> in <c>m_savedProjections</c>, and
    /// <c>UpdateOnceBeforeFrame</c> then clones every one of them, remaps their ids and builds the
    /// preview clipboard in a single call. <c>Naginata.sbc</c> is 47,187 blocks against a
    /// 2,026-block hull; 30 of the 37 prefab files with a projector carry a loaded blueprint.</para>
    ///
    /// <para><b>Why nothing is lost.</b> An NPC hull never welds from its projection. Clearing the
    /// field also keeps the blueprint out of the save: <c>GetObjectBuilderCubeBlock</c> writes
    /// <c>m_savedProjections</c> back out for as long as it is set.</para>
    ///
    /// <para><b>Timing.</b> Called from the fleet spawn callback, which runs from
    /// <c>Parallel.RunCallbacks</c> ahead of the session components that run
    /// <c>UpdateOnceBeforeFrame</c> (or synchronously, for a captured prefab), so the field is cleared
    /// before the projector reads it. Only fleet hulls pass through here; stations and SE's own
    /// spawners are untouched.</para>
    /// </summary>
    internal static class NpcProjections
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static readonly FieldInfo SavedProjections =
            typeof(MyProjectorBase).GetField("m_savedProjections", BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool _warned;

        /// <summary>Clears every projector's saved blueprint on the spawned grids. Returns how many
        /// projectors held one. Never throws: a failure here must not lose the spawn.</summary>
        internal static int ClearSaved(IList<MyCubeGrid> grids)
        {
            if (grids == null || SavedProjections == null)
            {
                if (SavedProjections == null && !_warned)
                {
                    _warned = true;
                    Log.Warn("Fleet spawn: MyProjectorBase.m_savedProjections not found; NPC hull projections are not cleared.");
                }
                return 0;
            }
            int cleared = 0;
            try
            {
                foreach (MyCubeGrid grid in grids)
                {
                    if (grid == null || grid.MarkedForClose) continue;
                    foreach (MyCubeBlock block in grid.GetFatBlocks())
                    {
                        var projector = block as MyProjectorBase;
                        if (projector == null) continue;
                        var saved = SavedProjections.GetValue(projector) as System.Collections.ICollection;
                        if (saved == null || saved.Count == 0) continue;
                        SavedProjections.SetValue(projector, null);
                        cleared++;
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_warned) { _warned = true; Log.Warn("Fleet spawn: clearing projections failed (" + ex.Message + ")."); }
            }
            return cleared;
        }
    }
}
