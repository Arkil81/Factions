using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Sandbox.Game.Entities;
using Sandbox.Game.World;

namespace Factions
{
    /// <summary>
    /// <c>factions bench gc</c>: a blocking, compacting full collection, reported beside the entity counts that
    /// could be holding what survives it. <c>gcMemoryMB</c> in telemetry includes garbage and the survived figure
    /// is only the live heap after a gen-2, so a heap that grows with distance flown cannot otherwise be told from
    /// one that has not been collected yet. The pause is the whole heap's mark-and-compact on the game thread:
    /// seconds on a heap of several GB. Test server only.
    /// </summary>
    internal static class BenchGc
    {
        internal static string Run()
        {
            long before = GC.GetTotalMemory(false);
            long t0 = Stopwatch.GetTimestamp();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            long after = GC.GetTotalMemory(false);

            int entities = 0, grids = 0, voxels = 0;
            try
            {
                foreach (var e in MyEntities.GetEntities())
                {
                    entities++;
                    if (e is MyCubeGrid) grids++;
                }
                if (MySession.Static != null && MySession.Static.VoxelMaps != null)
                    voxels = MySession.Static.VoxelMaps.Instances.Count;
            }
            catch { }
            FeatureRockMaterializer rock = FeatureRockMaterializer.Active;

            return "bench gc: heapMB " + Mb(before) + " -> " + Mb(after)
                + " pauseMs=" + ms.ToString("0", CultureInfo.InvariantCulture)
                + " entities=" + entities + " grids=" + grids + " voxelMaps=" + voxels
                + " rockCells=" + (rock != null ? rock.LiveCells.ToString(CultureInfo.InvariantCulture) : "-")
                + " rockPlanned=" + (rock != null ? rock.PlannedObjects.ToString(CultureInfo.InvariantCulture) : "-")
                + " rockSpawned=" + (rock != null ? rock.LiveObjects.ToString(CultureInfo.InvariantCulture) : "-")
                + " clipmapsSwept=" + ClipmapWaitSweep.Swept.ToString(CultureInfo.InvariantCulture)
                + " " + RockRetention.Census();
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
