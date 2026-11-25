using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Factions.Core;
using HarmonyLib;
using NLog;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Factions
{
    /// <summary>
    /// Takes materialised stations nobody is near out of SE's entity updates.
    ///
    /// <para>A station is quiet while no player or phantom is within <see cref="FactionTuning.StationQuietKm"/> and no
    /// moving grid other than its own is within <see cref="FactionTuning.StationQuietWakeCombatKm"/>. Quieting
    /// unregisters the station's grids, blocks and their subparts from <c>MyParallelEntityUpdateOrchestrator</c>; a
    /// prefix on its <c>AddEntityInternal</c> keeps them out, because any later flag change re-registers an entity.
    /// Nothing else changes: the grids keep their collision, take damage, and replicate. Measured with 15 phantom
    /// patrols: entity updates (<c>MySector</c>) 2.7 -> 4.6 ms/frame without hulls, most of it standing stations
    /// (up to 133 station grids) that no observer was near.</para>
    /// </summary>
    internal static class StationQuiet
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private const int TickFrames = 30;

        /// <summary>Entity ids of the grids of every quiet station (its whole mechanical group).</summary>
        private static readonly HashSet<long> QuietGrids = new HashSet<long>();
        /// <summary>Station grid id -> the grid ids quieted with it.</summary>
        private static readonly Dictionary<long, List<long>> ByStation = new Dictionary<long, List<long>>();
        /// <summary>Scratch lists of grids, emptied after each use so a closed grid is never held here.</summary>
        private static readonly List<IMyCubeGrid> GroupScratch = new List<IMyCubeGrid>();
        private static readonly List<MyEntity> Movers = new List<MyEntity>();
        private static readonly List<long> DropScratch = new List<long>();
        private static readonly HashSet<long> StandingScratch = new HashSet<long>();
        private static int _frame, _quieted, _woken, _standing, _nearObserver, _nearMover;
        private static long _tickTicks, _reportStart;
        private static bool _patched;

        internal static Func<IList<Vec3>> ObserversKm;

        internal static int QuietCount { get { return ByStation.Count; } }

        internal static void TryPatch(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo target = AccessTools.Method(typeof(MyParallelEntityUpdateOrchestrator), "AddEntityInternal");
                if (target == null) { Log.Error("Station quiet: MyParallelEntityUpdateOrchestrator.AddEntityInternal did not resolve; stations are never quieted."); return; }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(StationQuiet).GetMethod("AddPrefix",
                    BindingFlags.NonPublic | BindingFlags.Static)));
                _patched = true;
                Log.Info("Station quiet: orchestrator gate bound.");
            }
            catch (Exception ex)
            {
                Log.Error("Station quiet: could not patch AddEntityInternal: " + ex.Message);
            }
        }

        private static bool AddPrefix(MyEntity entity)
        {
            if (QuietGrids.Count == 0 || entity == null) return true;
            try
            {
                IMyEntity top = entity.GetTopMostParent();
                return top == null || !QuietGrids.Contains(top.EntityId);
            }
            catch { return true; }
        }

        internal static void Tick(StationMaterializer stations, FactionTuning tuning)
        {
            if (++_frame % TickFrames != 0) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (!_patched || stations == null || tuning == null)
                {
                    if (ByStation.Count > 0) WakeAll();
                    return;
                }
                IList<Vec3> observers = ObserversKm != null ? ObserversKm() : null;
                double quietKm = Math.Max(0, tuning.StationQuietKm);
                double wakeKm2 = quietKm * quietKm;
                double sleepKm = quietKm + Math.Max(0, tuning.StationQuietHysteresisKm);
                double sleepKm2 = sleepKm * sleepKm;
                double combatM = Math.Max(0, tuning.StationQuietWakeCombatKm) * 1000.0;

                Movers.Clear();
                if (combatM > 0) RockContactPhysics.CollectMovers(Movers);

                StandingScratch.Clear();
                _standing = _nearObserver = _nearMover = 0;
                foreach (KeyValuePair<string, long> kv in stations.MaterializedGrids())
                {
                    long gridId = kv.Value;
                    StandingScratch.Add(gridId);
                    MyCubeGrid grid = MyEntities.GetEntityById(gridId) as MyCubeGrid;
                    if (grid == null || grid.Closed || grid.MarkedForClose) continue;
                    Vector3D p = grid.PositionComp.WorldAABB.Center;
                    var posKm = new Vec3(p.X / 1000.0, p.Y / 1000.0, p.Z / 1000.0);
                    double nearest2 = double.MaxValue;
                    if (observers != null)
                        for (int i = 0; i < observers.Count; i++)
                        {
                            double d2 = observers[i].DistanceSquaredTo(posKm);
                            if (d2 < nearest2) nearest2 = d2;
                        }
                    bool quiet = ByStation.ContainsKey(gridId);
                    bool combat = combatM > 0 && MoverNear(grid, p, combatM);
                    _standing++;
                    if (nearest2 <= sleepKm2) _nearObserver++;
                    else if (combat) _nearMover++;
                    if (quiet && (nearest2 <= wakeKm2 || combat)) Wake(gridId);
                    else if (!quiet && nearest2 > sleepKm2 && !combat) Quiet(grid);
                }

                DropScratch.Clear();
                foreach (long id in ByStation.Keys) if (!StandingScratch.Contains(id)) DropScratch.Add(id);
                for (int i = 0; i < DropScratch.Count; i++) Wake(DropScratch[i]);
            }
            catch (Exception ex)
            {
                Log.Warn("Station quiet tick failed: " + ex.Message);
            }
            finally
            {
                Movers.Clear();
                _tickTicks += Stopwatch.GetTimestamp() - t0;
                if (StationMaterializer.DebugEnabled) Report();
            }
        }

        private static bool MoverNear(MyCubeGrid station, Vector3D centre, double rangeM)
        {
            double r2 = rangeM * rangeM;
            for (int i = 0; i < Movers.Count; i++)
            {
                var g = Movers[i] as MyCubeGrid;
                if (g == null || g == station) continue;
                if (g.Physics == null || g.Physics.LinearVelocity.LengthSquared() < 1.0) continue;
                if (Vector3D.DistanceSquared(g.PositionComp.WorldAABB.Center, centre) <= r2) return true;
            }
            return false;
        }

        private static void Quiet(MyCubeGrid station)
        {
            GroupScratch.Clear();
            try { ((IMyCubeGrid)station).GetGridGroup(GridLinkTypeEnum.Mechanical).GetGrids(GroupScratch); }
            catch { }
            if (GroupScratch.Count == 0) GroupScratch.Add(station);
            var ids = new List<long>(GroupScratch.Count);
            try
            {
                for (int i = 0; i < GroupScratch.Count; i++)
                {
                    var g = GroupScratch[i] as MyCubeGrid;
                    if (g == null || g.Closed) continue;
                    ids.Add(g.EntityId);
                    QuietGrids.Add(g.EntityId);
                    ForEachInHierarchy(g, e => MyEntities.UnregisterForUpdate(e));
                }
            }
            finally
            {
                GroupScratch.Clear();
            }
            ByStation[station.EntityId] = ids;
            _quieted++;
        }

        private static void Wake(long stationGridId)
        {
            List<long> ids;
            if (!ByStation.TryGetValue(stationGridId, out ids)) return;
            ByStation.Remove(stationGridId);
            for (int i = 0; i < ids.Count; i++) QuietGrids.Remove(ids[i]);
            for (int i = 0; i < ids.Count; i++)
            {
                MyEntity g;
                if (!MyEntities.TryGetEntityById(ids[i], out g) || g.Closed || g.MarkedForClose) continue;
                ForEachInHierarchy(g, e => MyEntities.RegisterForUpdate(e));
            }
            _woken++;
        }

        private static void WakeAll()
        {
            DropScratch.Clear();
            DropScratch.AddRange(ByStation.Keys);
            for (int i = 0; i < DropScratch.Count; i++) Wake(DropScratch[i]);
        }

        private static void ForEachInHierarchy(MyEntity e, Action<MyEntity> act)
        {
            if (e == null || e.Closed) return;
            act(e);
            var grid = e as MyCubeGrid;
            if (grid != null)
                foreach (MyCubeBlock b in grid.GetFatBlocks()) ForEachInHierarchy(b, act);
            else if (e.Subparts != null)
                foreach (KeyValuePair<string, MyEntitySubpart> sp in e.Subparts) ForEachInHierarchy(sp.Value, act);
        }

        private static void Report()
        {
            long now = Stopwatch.GetTimestamp();
            if (_reportStart == 0) { _reportStart = now; return; }
            if (now - _reportStart < 10 * Stopwatch.Frequency) return;
            Log.Info("STATION_QUIET standing=" + _standing + " awakeObserver=" + _nearObserver + " awakeMover=" + _nearMover
                     + " quiet=" + ByStation.Count + " grids=" + QuietGrids.Count
                     + " quietedWin=" + _quieted + " wokenWin=" + _woken
                     + " tickMsTotal=" + (_tickTicks * 1000.0 / Stopwatch.Frequency).ToString("0.00", CultureInfo.InvariantCulture));
            _reportStart = now;
            _quieted = _woken = 0;
            _tickTicks = 0;
        }
    }
}
