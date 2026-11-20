using System;
using Sandbox.Game.EntityComponents.Renders;

namespace Factions
{
    /// <summary>
    /// Empties SE's <c>MyRenderComponentVoxelMap.VoxelLoadingWaitStep.Clipmaps</c> on a dedicated server.
    /// <para><c>AddRenderObjects</c> adds every voxel map's clipmap to that static set, and only the clipmap's
    /// <c>Loaded</c> event removes it. That event is raised by the renderer, which a dedicated server does not run, so
    /// every voxel map ever added to the scene stays reachable through clipmap -> mesher -> entity after it closes:
    /// ~32 KB of managed heap per feature rock built and closed, measured with <c>factions bench roots</c>. A server
    /// never reads the set (<c>AreClipmapsReady</c> is forced true when dedicated), so emptying it changes nothing
    /// else.</para>
    /// </summary>
    internal static class ClipmapWaitSweep
    {
        private const int SweepFrames = 600;
        private static int _frame;

        /// <summary>Clipmaps dropped since the process started.</summary>
        internal static long Swept;

        internal static void Tick()
        {
            if (++_frame % SweepFrames != 0) return;
            if (!Sandbox.Engine.Platform.Game.IsDedicated) return;
            try
            {
                var set = MyRenderComponentVoxelMap.VoxelLoadingWaitStep.Clipmaps;
                lock (set)
                {
                    if (set.Count == 0) return;
                    Swept += set.Count;
                    set.Clear();
                }
            }
            catch { }
        }
    }
}
